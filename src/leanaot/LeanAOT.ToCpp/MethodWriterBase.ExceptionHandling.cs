using dnlib.DotNet;
using dnlib.DotNet.Emit;
using LeanAOT.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LeanAOT.ToCpp
{
    // try / catch / finally / fault in compiled methods (filters stay interpreted: see MetaUtil.GetExceptionHandlerSkipReason).
    //
    // Everything stays inside the one C++ function. Every error path already funnels into ___label_handle_return_error with
    // the IL offset of the failing instruction in ___ret_ip; in a method with handlers that label becomes a dispatcher that
    // walks the clauses in table order exactly like the interpreter's unwind loop (interpreter.cpp, unwind_exception_handler):
    // the first clause whose try range holds the offset and that is a matching catch, or a finally / fault, gets control.
    //  - catch: the exception is the handler block's one input stack value; leaving the handler clears it as the current
    //    exception (pop_throw_flow). rethrow raises the exception of the innermost catch handler holding the instruction.
    //  - finally / fault entered by an exception: the exception and its offset are saved per clause, the continuation is -1,
    //    and endfinally resumes the search at the next clause.
    //  - leave: runs the finally clauses whose try holds the leave and not its target, in clause order (the interpreter's
    //    find_finally_clause_idx_of_leave_target), through per-clause continuation numbers that endfinally switches on.
    //  - nothing matches: the exception becomes the current exception again and the method returns ManagedException.
    partial class MethodWriterBase
    {
        private sealed class EhClause
        {
            public int Index;
            public ExceptionHandlerType Kind;
            public uint TryStart, TryEnd, HandlerStart, HandlerEnd;
            public BasicBlock HandlerBlock;
            public string CatchKlassExpr;          // catch: RtClass* to test, null when it catches everything (System.Object)
            public EvalVariable CaughtVar;         // catch: the exception being handled (rethrow, leaving the handler)
            public EvalVariable HandlerInputVar;   // catch: the handler block's input stack slot
            public EvalVariable SavedExVar;        // finally / fault: the exception in flight while the handler runs
            public string SavedIpVar;              // finally / fault: its IL offset
            public string ContVar;                 // finally: -1 = entered by an exception, else a continuation number
            public readonly List<EhContinuation> Continuations = new List<EhContinuation>();

            public bool IsFinallyOrFault => Kind == ExceptionHandlerType.Finally || Kind == ExceptionHandlerType.Fault;

            public bool TryContains(uint offset) => TryStart <= offset && offset < TryEnd;

            public bool HandlerContains(uint offset) => HandlerStart <= offset && offset < HandlerEnd;
        }

        // where endfinally of a clause goes when it was entered by a leave
        private sealed class EhContinuation
        {
            public EhClause NextFinally;   // null: the leave target
            public int NextContId;
            public BasicBlock Target;
        }

        private sealed class EhLeave
        {
            public EhClause LeftCatch;     // innermost catch handler this leave exits, if any
            public EhClause FirstFinally;  // null: no finally to run
            public int FirstContId;
            public BasicBlock Target;
        }

        private List<EhClause> _ehClauses;   // null: the method has no exception handlers
        private Dictionary<Instruction, EhLeave> _ehLeaves;
        private EvalVariable _ehExVar;
        private const string EhIpVarName = "__eh_ip";
        private readonly HashSet<int> _ehSearchLabelsUsed = new HashSet<int>();

        private bool HasExceptionHandlers => _ehClauses != null;

        private static string GetEhSearchLabelName(int clauseIndex)
        {
            return $"__eh_search_{clauseIndex}";
        }

        private string GetAotRootSyncStatement(EvalVariable var, string valueExpr)
        {
            if (var.type != EvalDataType.Ref || !_aotRootSlots.TryGetValue(var.varId, out int slot))
            {
                return null;
            }
            return $"__leanclr_aot_roots[{slot}].obj = ({ConstStrings.ObjectPtrTypeName})({valueExpr});";
        }

        private EvalVariable DeclareEhObjectVariable(bool inOutVar = false, TypeSig typeSig = null)
        {
            EvalVariable var = NewEvalVariable(EvalDataType.Ref, typeSig ?? _corlibTypes.Object, inOutVar);
            _localsWriter.AddLine($"{ConstStrings.ObjectPtrTypeName} {GetEvalVariableName(var)} = nullptr;");
            return var;
        }

        /// <summary>Runs before any block is written: clause table, handler entry stacks, leave chains.</summary>
        private void PrepareExceptionHandling(CilBody body)
        {
            if (!body.HasExceptionHandlers)
            {
                return;
            }
            // the dispatcher raises through this method's frame, so the error label must exist
            _ = CurMethodVar;
            _ehClauses = new List<EhClause>();
            _ehLeaves = new Dictionary<Instruction, EhLeave>();
            _localsWriter.AddLine("// Exception handling");
            _ehExVar = DeclareEhObjectVariable();
            _localsWriter.AddLine($"int32_t {EhIpVarName} = 0;");

            int index = 0;
            foreach (ExceptionHandler eh in body.ExceptionHandlers)
            {
                var clause = new EhClause
                {
                    Index = index,
                    Kind = eh.HandlerType,
                    TryStart = eh.TryStart.Offset,
                    TryEnd = eh.TryEnd != null ? eh.TryEnd.Offset : _codeSize,
                    HandlerStart = eh.HandlerStart.Offset,
                    HandlerEnd = eh.HandlerEnd != null ? eh.HandlerEnd.Offset : _codeSize,
                    HandlerBlock = _instToBbMap[eh.HandlerStart],
                };
                EvalStackState handlerState = _blockEvalStackStates[clause.HandlerBlock];
                if (handlerState.inputStackSetup)
                {
                    throw new NotSupportedException($"Handler block shared by two clauses in method: {_method.FullName}.");
                }
                handlerState.inputStackSetup = true;
                switch (eh.HandlerType)
                {
                case ExceptionHandlerType.Catch:
                {
                    ITypeDefOrRef catchType = _method.InflateType(eh.CatchType);
                    // in an instantiation the catch type resolves through its token with the method's generic context, like
                    // an instruction operand
                    MapGenericOperandToken(catchType, eh.CatchType.MDToken.ToUInt32());
                    if (catchType.FullName != "System.Object")
                    {
                        _forwardDeclaration.AddTypeForwardDefine(catchType);
                        TypeDetail typeDetail = _metadataService.GetTypeDetail(catchType);
                        clause.CatchKlassExpr = _runtimeResolvedMetadatas.GetTypeVariable(catchType, typeDetail).GetFullReferenceVariableName();
                    }
                    clause.CaughtVar = DeclareEhObjectVariable();
                    clause.HandlerInputVar = DeclareEhObjectVariable(true, catchType.ToTypeSig());
                    handlerState.inputStackDatas.Add(clause.HandlerInputVar);
                    break;
                }
                case ExceptionHandlerType.Finally:
                case ExceptionHandlerType.Fault:
                {
                    clause.SavedExVar = DeclareEhObjectVariable();
                    clause.SavedIpVar = $"__eh_ip{index}";
                    clause.ContVar = $"__eh_cont{index}";
                    _localsWriter.AddLine($"int32_t {clause.SavedIpVar} = 0;");
                    _localsWriter.AddLine($"int32_t {clause.ContVar} = -1;");
                    break;
                }
                default:
                    throw new NotSupportedException($"Exception clause {eh.HandlerType} is not supported in method: {_method.FullName}.");
                }
                _ehClauses.Add(clause);
                index++;
            }

            foreach (Instruction inst in body.Instructions)
            {
                if (inst.OpCode.Code != Code.Leave && inst.OpCode.Code != Code.Leave_S)
                {
                    continue;
                }
                var targetInst = (Instruction)inst.Operand;
                uint src = inst.Offset;
                uint dst = targetInst.Offset;
                var leave = new EhLeave
                {
                    Target = _instToBbMap[targetInst],
                    LeftCatch = _ehClauses.FirstOrDefault(c => c.Kind == ExceptionHandlerType.Catch && c.HandlerContains(src) && !c.HandlerContains(dst)),
                    FirstContId = -1,
                };
                List<EhClause> chain = _ehClauses.Where(c => c.Kind == ExceptionHandlerType.Finally && c.TryContains(src) && !c.TryContains(dst)).ToList();
                for (int k = chain.Count - 1; k >= 0; k--)
                {
                    int id = GetOrAddEhContinuation(chain[k], leave.FirstFinally, leave.FirstContId, leave.Target);
                    leave.FirstFinally = chain[k];
                    leave.FirstContId = id;
                }
                _ehLeaves.Add(inst, leave);
            }
        }

        private static int GetOrAddEhContinuation(EhClause clause, EhClause nextFinally, int nextContId, BasicBlock target)
        {
            for (int i = 0; i < clause.Continuations.Count; i++)
            {
                EhContinuation c = clause.Continuations[i];
                if (c.NextFinally == nextFinally && c.NextContId == nextContId && c.Target == target)
                {
                    return i;
                }
            }
            clause.Continuations.Add(new EhContinuation { NextFinally = nextFinally, NextContId = nextContId, Target = target });
            return clause.Continuations.Count - 1;
        }

        private EhClause FindInnermostHandlerClause(uint offset, Func<EhClause, bool> predicate)
        {
            // nested clauses come before the clauses enclosing them (ECMA-335 II.19)
            return _ehClauses?.FirstOrDefault(c => predicate(c) && c.HandlerContains(offset));
        }

        private void EmitLeaveImpl(Instruction inst, Instruction target)
        {
            // leave empties the evaluation stack
            _curState.runStackDatas.Clear();
            if (_ehLeaves == null || !_ehLeaves.TryGetValue(inst, out EhLeave leave))
            {
                EmitGotoWithSetupInOutVars(_instToBbMap[target]);
                return;
            }
            if (leave.LeftCatch != null)
            {
                _bodyWriter.AddLine($"{VmFunctionNames.AotEhLeaveCatch}({GetEvalVariableName(leave.LeftCatch.CaughtVar)});");
            }
            if (leave.FirstFinally == null)
            {
                EmitGotoWithSetupInOutVars(leave.Target);
                return;
            }
            _bodyWriter.AddLine($"{leave.FirstFinally.ContVar} = {leave.FirstContId};");
            EmitGotoWithSetupInOutVars(leave.FirstFinally.HandlerBlock);
        }

        private void EmitResumeExceptionSearch(EhClause clause)
        {
            _bodyWriter.AddLine($"{GetEvalVariableName(_ehExVar)} = {GetEvalVariableName(clause.SavedExVar)};");
            string sync = GetAotRootSyncStatement(_ehExVar, GetEvalVariableName(_ehExVar));
            if (sync != null)
            {
                _bodyWriter.AddLine(sync);
            }
            _bodyWriter.AddLine($"{EhIpVarName} = {clause.SavedIpVar};");
            _ehSearchLabelsUsed.Add(clause.Index + 1);
            _bodyWriter.AddLine($"goto {GetEhSearchLabelName(clause.Index + 1)};");
        }

        private void EmitEndfinallyImpl(Instruction inst)
        {
            // endfinally empties the evaluation stack
            _curState.runStackDatas.Clear();
            EhClause clause = FindInnermostHandlerClause(inst.Offset, c => c.IsFinallyOrFault);
            if (clause == null)
            {
                throw new NotSupportedException($"endfinally outside a finally / fault handler at IL_{inst.Offset:X4} in method: {_method.FullName}.");
            }
            if (clause.Kind == ExceptionHandlerType.Fault || clause.Continuations.Count == 0)
            {
                EmitResumeExceptionSearch(clause);
                return;
            }
            _bodyWriter.AddLine($"switch ({clause.ContVar})");
            _bodyWriter.AddLine("{");
            for (int i = 0; i < clause.Continuations.Count; i++)
            {
                EhContinuation cont = clause.Continuations[i];
                _bodyWriter.AddLine($"case {i}:");
                _bodyWriter.IncreaseIndent();
                if (cont.NextFinally != null)
                {
                    _bodyWriter.AddLine($"{cont.NextFinally.ContVar} = {cont.NextContId};");
                    EmitGotoWithSetupInOutVars(cont.NextFinally.HandlerBlock);
                }
                else
                {
                    EmitGotoWithSetupInOutVars(cont.Target);
                }
                _bodyWriter.DecreaseIndent();
            }
            _bodyWriter.AddLine("default:");
            _bodyWriter.IncreaseIndent();
            EmitResumeExceptionSearch(clause);
            _bodyWriter.DecreaseIndent();
            _bodyWriter.AddLine("}");
        }

        private void EmitRethrowImpl(Instruction inst)
        {
            EhClause clause = FindInnermostHandlerClause(inst.Offset, c => c.Kind == ExceptionHandlerType.Catch);
            if (clause == null)
            {
                // the interpreter raises ExecutionEngine for a rethrow with no exception being handled
                EmitThrowRuntimeError(inst, "ExecutionEngine");
                return;
            }
            _bodyWriter.AddLine($"{VmFunctionNames.GOTO_RETHROW_EXCEPTION}({GetEvalVariableName(clause.CaughtVar)}, {CurMethodVar.GetFullReferenceVariableName()}, {GetCurrentIpOffset(inst)});");
        }

        /// <summary>The body of ___label_handle_return_error in a method with exception handlers.</summary>
        private void WriteExceptionDispatcher()
        {
            string ex = GetEvalVariableName(_ehExVar);
            _tailWriter.AddLine($"{ex} = {VmFunctionNames.AotEhTakeException}({GetRetErrorVariableName()}, {CurMethodVar.GetFullReferenceVariableName()}, {GetRetErrorIpVariableName()});");
            _tailWriter.AddLine($"if ({VmFunctionNames.Unlikely}({ex} == nullptr))");
            _tailWriter.AddLine("{");
            _tailWriter.AddLine($"    return {ConstStrings.RtErrTypeName}::ManagedException;");
            _tailWriter.AddLine("}");
            string exSync = GetAotRootSyncStatement(_ehExVar, ex);
            if (exSync != null)
            {
                _tailWriter.AddLine(exSync);
            }
            _tailWriter.AddLine($"{EhIpVarName} = {GetRetErrorIpVariableName()};");
            foreach (EhClause clause in _ehClauses)
            {
                if (_ehSearchLabelsUsed.Contains(clause.Index))
                {
                    _tailWriter.AddLine($"{GetEhSearchLabelName(clause.Index)}:");
                }
                _tailWriter.AddLine($"// clause {clause.Index}: {clause.Kind} try IL_{clause.TryStart:X4}-IL_{clause.TryEnd:X4} handler IL_{clause.HandlerStart:X4}");
                _tailWriter.AddLine($"if ({EhIpVarName} >= {clause.TryStart} && {EhIpVarName} < {clause.TryEnd}{(clause.CatchKlassExpr != null ? $" && {VmFunctionNames.IsAssignableFrom}({ex}->klass, {clause.CatchKlassExpr})" : "")})");
                _tailWriter.AddLine("{");
                _tailWriter.IncreaseIndent();
                if (clause.Kind == ExceptionHandlerType.Catch)
                {
                    foreach (EvalVariable var in new[] { clause.CaughtVar, clause.HandlerInputVar })
                    {
                        _tailWriter.AddLine($"{GetEvalVariableName(var)} = {ex};");
                        string sync = GetAotRootSyncStatement(var, ex);
                        if (sync != null)
                        {
                            _tailWriter.AddLine(sync);
                        }
                    }
                }
                else
                {
                    _tailWriter.AddLine($"{GetEvalVariableName(clause.SavedExVar)} = {ex};");
                    string sync = GetAotRootSyncStatement(clause.SavedExVar, ex);
                    if (sync != null)
                    {
                        _tailWriter.AddLine(sync);
                    }
                    _tailWriter.AddLine($"{clause.SavedIpVar} = {EhIpVarName};");
                    _tailWriter.AddLine($"{clause.ContVar} = -1;");
                }
                _tailWriter.AddLine($"goto {GetBasicBlockStartLabelName(clause.HandlerBlock)};");
                _tailWriter.DecreaseIndent();
                _tailWriter.AddLine("}");
            }
            if (_ehSearchLabelsUsed.Contains(_ehClauses.Count))
            {
                _tailWriter.AddLine($"{GetEhSearchLabelName(_ehClauses.Count)}:");
            }
            _tailWriter.AddLine($"return {VmFunctionNames.AotEhUnhandled}({ex});");
        }
    }
}
