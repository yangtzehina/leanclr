using dnlib.DotNet;
using dnlib.DotNet.Emit;
using LeanAOT.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LeanAOT.ToCpp
{
    partial class MethodWriterBase
    {
        private bool TryEmitCallInstrinsic(Instruction inst, MethodDetail methodDetail, Func<string> methodVarNameProvider, List<EvalVariable> args, EvalVariable retVar)
        {
            MethodDef methodDef = methodDetail.MethodDef;
            if (methodDef == null)
            {
                TypeSig declaringTypeSig = methodDetail.Method.DeclaringType.ToTypeSig();
                if (declaringTypeSig.ElementType != ElementType.Array)
                {
                    throw new Exception($"impossible: methodDef is null but the declaring type is not an array. method:{methodDetail.Method}");
                }
                switch (methodDetail.Method.Name)
                {
                case "Get":
                {
                    EmitCallMdArrayGetIntrinsic(inst, methodDetail, methodVarNameProvider(), args, retVar);
                    return true;
                }
                case "Set":
                {
                    EmitCallMdArraySetIntrinsic(inst, methodDetail, methodVarNameProvider(), args, retVar);
                    return true;
                }
                case "Address":
                {
                    EmitCallMdArrayAddressIntrinsic(inst, methodDetail, methodVarNameProvider(), args, retVar);
                    return true;
                }
                }
                return false;
            }

            if (!GlobalServices.Inst.IsCoreLibraryModule(methodDef.Module))
            {
                return false;
            }

            if (TryEmitInlineRuntimeIntrinsic(inst, methodDetail, methodVarNameProvider, args, retVar))
            {
                return true;
            }

            UTF8String methodName = methodDef.Name;
            TypeDef declaringType = methodDef.DeclaringType;
            switch (declaringType.Name.ToString())
            {
            case "Object":
            {
                if (methodName == VmFunctionNames.Ctor)
                {
                    // System.Object's .ctor is an empty method, we can just ignore the call to it.
                    return true;
                }
                break;
            }
            case "String":
            {
                break;
            }
            case "Assembly":
            {
                if (methodName == "GetExecutingAssembly")
                {
                    EmitAssignOrThrow(inst, retVar, $"{ConstStrings.CodegenNamespace}::get_assembly_reflection_object({CurMethodVar.GetFullReferenceVariableName()}->parent->image)");
                    return true;
                }
                break;
            }
            case "MethodBase":
            {
                if (methodName == "GetCurrentMethod")
                {
                    EmitAssignOrThrow(inst, retVar, $"{ConstStrings.CodegenNamespace}::get_method_reflection_object({CurMethodVar.GetFullReferenceVariableName()})");
                    return true;
                }
                break;
            }
            default:
                break;
            }

            if (TryEmitRuntimeHelpersCreateSpan(inst, methodDef, args, retVar))
            {
                return true;
            }
            
            
            var runtimeApiCatalog = GlobalServices.Inst.RuntimeApiCatalog;
            if (runtimeApiCatalog.TryGetIcallOrIntrinsic(methodDef, out var entry, out var _methodKind))
            {
                EmitCallICallOrIntrinsic(inst, methodDetail, entry, args, retVar);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Runtime-implemented CoreLib helpers (Unsafe, RuntimeHelpers type predicates, MemoryMarshal.GetArrayDataReference)
        /// written out in place. Without this a compiled caller reaches them through a direct-call bridge and the runtime's
        /// intrinsic invoker (arguments copied to an eval-stack buffer and back), which costs more than the operation; the
        /// expressions here do what src/runtime/intrinsics/system_runtime_compilerservices_unsafe.cpp and
        /// system_runtime_interopservices_memorymarshal.cpp do. Anything not matched exactly keeps the old path.
        /// </summary>
        private bool TryEmitInlineRuntimeIntrinsic(Instruction inst, MethodDetail methodDetail, Func<string> methodVarNameProvider, List<EvalVariable> args, EvalVariable retVar)
        {
            MethodDef def = methodDetail.MethodDef;
            string typeName = def.DeclaringType.FullName;
            string name = def.Name;
            IList<TypeSig> ps = def.MethodSig.Params;
            IList<TypeSig> gargs = methodDetail.Method is MethodSpec spec ? spec.GenericInstMethodSig.GenericArguments : null;
            string A(int i) => GetEvalVariableName(args[i]);
            if (!def.IsStatic)
            {
                if (typeName == "System.Type" && name == "get_IsValueType" && ps.Count == 0 && retVar != null)
                {
                    // typeof(TKey).IsValueType (Dictionary.FindValue): fixed for a RuntimeType; `this` was null-checked by the call
                    string site = $"{GetEvalVariableName(retVar)}_site";
                    _bodyWriter.AddLine($"static leanclr::codegen::AotTypeIsValueTypeSite {site};");
                    EmitAssignOrThrow(inst, retVar, $"leanclr::codegen::type_is_value_type_cached((leanclr::vm::RtObject*)({A(0)}), {site})");
                    return true;
                }
                if ((typeName == "System.Span`1" || typeName == "System.ReadOnlySpan`1") && TryEmitSpanMember(inst, methodDetail, name, ps, args, retVar))
                {
                    return true;
                }
                return false;
            }

            void Assign(string expr)
            {
                _bodyWriter.AddLine($"{GetEvalVariableName(retVar)} = ({GetTypeName(retVar)})({expr});");
                EmitSyncAotRoot(retVar);
            }

            if (typeName == "System.Runtime.CompilerServices.Unsafe")
            {
                switch (name)
                {
                case "SizeOf":
                    if (gargs?.Count == 1 && ps.Count == 0 && retVar != null && TryGetElementSizeExpr(gargs[0], out string sizeOfT))
                    {
                        Assign($"(int32_t){sizeOfT}");
                        return true;
                    }
                    return false;
                case "As":
                case "AsRef":
                case "AsPointer":
                    // the same reference or address under another type (Unsafe::as / as_pointer return their argument)
                    if (ps.Count == 1 && retVar != null)
                    {
                        Assign(A(0));
                        return true;
                    }
                    return false;
                case "Add":
                case "Subtract":
                    if (gargs?.Count == 1 && ps.Count == 2 && retVar != null && TryGetElementSizeExpr(gargs[0], out string elementSize))
                    {
                        string offset = GetIntegerOffsetExpr(ps[1], A(1));
                        if (offset == null)
                        {
                            return false;
                        }
                        Assign($"(uint8_t*)({A(0)}) {(name == "Add" ? "+" : "-")} {offset} * (intptr_t){elementSize}");
                        return true;
                    }
                    return false;
                case "AddByteOffset":
                case "SubtractByteOffset":
                    if (ps.Count == 2 && retVar != null)
                    {
                        string offset = GetIntegerOffsetExpr(ps[1], A(1));
                        if (offset == null)
                        {
                            return false;
                        }
                        Assign($"(uint8_t*)({A(0)}) {(name == "AddByteOffset" ? "+" : "-")} {offset}");
                        return true;
                    }
                    return false;
                case "AreSame":
                    if (ps.Count == 2 && retVar != null)
                    {
                        Assign($"(void*)({A(0)}) == (void*)({A(1)})");
                        return true;
                    }
                    return false;
                case "IsAddressLessThan":
                case "IsAddressGreaterThan":
                    if (ps.Count == 2 && retVar != null)
                    {
                        Assign($"(uintptr_t)({A(0)}) {(name == "IsAddressLessThan" ? "<" : ">")} (uintptr_t)({A(1)})");
                        return true;
                    }
                    return false;
                case "ByteOffset":
                    if (ps.Count == 2 && retVar != null)
                    {
                        Assign($"(intptr_t)((uintptr_t)({A(1)}) - (uintptr_t)({A(0)}))");
                        return true;
                    }
                    return false;
                case "CopyBlock":
                case "CopyBlockUnaligned":
                    if (ps.Count == 3 && retVar == null)
                    {
                        _bodyWriter.AddLine($"std::memmove((void*)({A(0)}), (const void*)({A(1)}), (uint32_t)({A(2)}));");
                        return true;
                    }
                    return false;
                case "InitBlock":
                case "InitBlockUnaligned":
                    if (ps.Count == 3 && retVar == null)
                    {
                        _bodyWriter.AddLine($"std::memset((void*)({A(0)}), (uint8_t)({A(1)}), (uint32_t)({A(2)}));");
                        return true;
                    }
                    return false;
                case "SkipInit":
                    // a JIT hint; locals are already zeroed (Unsafe::skip_init does nothing either)
                    if (ps.Count == 1 && retVar == null)
                    {
                        return true;
                    }
                    return false;
                case "ReadUnaligned":
                    if (gargs?.Count == 1 && ps.Count == 1 && retVar != null && TryGetUnalignedPrimitive(gargs[0], out string readType))
                    {
                        string tmp = $"{GetEvalVariableName(retVar)}_raw";
                        _bodyWriter.AddLine($"{readType} {tmp};");
                        _bodyWriter.AddLine($"std::memcpy(&{tmp}, (const void*)({A(0)}), sizeof({readType}));");
                        Assign(tmp);
                        return true;
                    }
                    return false;
                case "WriteUnaligned":
                    if (gargs?.Count == 1 && ps.Count == 2 && retVar == null && TryGetUnalignedPrimitive(gargs[0], out string writeType))
                    {
                        string tmp = $"{A(1)}_raw";
                        _bodyWriter.AddLine($"{writeType} {tmp} = ({writeType})({A(1)});");
                        _bodyWriter.AddLine($"std::memcpy((void*)({A(0)}), &{tmp}, sizeof({writeType}));");
                        return true;
                    }
                    return false;
                default:
                    return false;
                }
            }
            if (typeName == "System.SpanHelpers" && ps.Count == 3 && retVar == null && name == "Memmove")
            {
                // [Intrinsic] Memmove(ref byte dest, ref byte src, nuint len), which LeanAOT does not compile from IL (it would
                // run interpreted): the same bytes moved, overlap allowed
                _bodyWriter.AddLine($"std::memmove((void*)({A(0)}), (const void*)({A(1)}), (size_t)({A(2)}));");
                return true;
            }
            if (typeName == "System.SpanHelpers" && ps.Count == 2 && retVar == null && name == "ClearWithoutReferences")
            {
                _bodyWriter.AddLine($"std::memset((void*)({A(0)}), 0, (size_t)({A(1)}));");
                return true;
            }
            if (typeName == "System.ArgumentNullException" && name == "ThrowIfNull" && ps.Count == 2 && retVar == null
                && ps[0].ElementType == ElementType.Object && ps[1].ElementType == ElementType.String)
            {
                // [Intrinsic] ThrowIfNull(object, string) (every lock statement's Monitor.Enter): nothing to do for a non-null
                // argument; a null one still goes to the real method, which builds the exception
                _bodyWriter.AddLine($"if ({GetUnlikelyExpr($"{A(0)} == nullptr")})");
                _bodyWriter.AddLine("{");
                _bodyWriter.IncreaseIndent();
                EmitCallByMethodPointerDirectly(inst, methodDetail, methodVarNameProvider(), args, null, false);
                _bodyWriter.DecreaseIndent();
                _bodyWriter.AddLine("}");
                return true;
            }
            if (typeName == "System.Type" && name == "GetTypeFromHandle" && ps.Count == 1 && retVar != null && args[0].type != EvalDataType.ValueType)
            {
                // typeof(T): the handle is a metadata pointer (ldtoken), the RuntimeType for it is fixed
                string site = $"{GetEvalVariableName(retVar)}_site";
                _bodyWriter.AddLine($"static leanclr::codegen::AotTypeFromHandleSite {site};");
                EmitAssignOrThrow(inst, retVar, $"leanclr::codegen::get_type_from_handle_cached((const void*)({A(0)}), {site})");
                return true;
            }
            if (typeName == "System.Runtime.CompilerServices.RuntimeHelpers")
            {
                string predicate = name == "IsReferenceOrContainsReferences" ? "leanclr::codegen::is_reference_or_contains_references_of"
                    : name == "IsBitwiseEquatable" ? "leanclr::codegen::is_bitwise_equatable_of" : null;
                if (predicate == null || gargs?.Count != 1 || ps.Count != 0 || retVar == null)
                {
                    return false;
                }
                string site = $"{GetEvalVariableName(retVar)}_site";
                _bodyWriter.AddLine($"static int8_t {site};");
                EmitAssignOrThrow(inst, retVar, $"leanclr::codegen::cached_type_predicate({site}, {predicate}, {methodVarNameProvider()})");
                return true;
            }
            if (typeName == "System.Runtime.InteropServices.MemoryMarshal" && name == "GetReference" && gargs?.Count == 1 && ps.Count == 1 && retVar != null
                && args[0].type == EvalDataType.ValueType && ps[0].RemovePinnedAndModifiers() is GenericInstSig spanParam
                && IsSpanLayout(spanParam.GenericType.TypeDefOrRef.ResolveTypeDef()))
            {
                // GetReference(Span<T>) / GetReference(ReadOnlySpan<T>): the span's _reference (layout as in TryEmitSpanMember)
                Assign($"({A(0)}).__field_0");
                return true;
            }
            if (typeName == "System.Runtime.InteropServices.MemoryMarshal" && name == "GetArrayDataReference" && gargs?.Count == 1 && ps.Count == 1 && retVar != null)
            {
                // get_array_data_reference: NullReference for null, else the first element's address (T[] is a vector)
                EmitCheckNotNull(inst, args[0], force: true);
                Assign($"{VmFunctionNames.GetSZArrayElementAddress}<uint8_t>((leanclr::vm::RtArray*)({A(0)}), 0)");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Span&lt;T&gt; / ReadOnlySpan&lt;T&gt;'s indexer, Length, IsEmpty and Slice written out in place. As calls, each one runs a
        /// compiled method with a metadata-init check, an AotFrameScope and an RtResult return, which in interpreted wasm costs
        /// several times the access itself: a loop over spans ran 3x slower than the same loop over arrays. The expressions are
        /// the IL bodies; `this` is the span's address, null-checked at the call like any instance call, and a failed check
        /// raises the exception type the IL throws (IndexOutOfRange from the indexer, ArgumentOutOfRange from Slice).
        /// </summary>
        private bool TryEmitSpanMember(Instruction inst, MethodDetail methodDetail, string name, IList<TypeSig> ps, List<EvalVariable> args, EvalVariable retVar)
        {
            if (retVar == null || !IsSpanLayout(methodDetail.MethodDef.DeclaringType)
                || !(methodDetail.Method.DeclaringType.ToTypeSig() is GenericInstSig spanSig) || spanSig.GenericArguments.Count != 1
                || !TryGetElementSizeExpr(spanSig.GenericArguments[0], out string elementSize))
            {
                return false;
            }
            bool IsI4(TypeSig t) => t.RemovePinnedAndModifiers().ElementType == ElementType.I4;
            _forwardDeclaration.AddTypeForwardDefine(spanSig);
            string span = $"(({GetExactTypeName(spanSig)}*)({GetEvalVariableName(args[0])}))";
            string ret = GetEvalVariableName(retVar);
            string retType = GetTypeName(retVar);
            void ThrowIf(string cond, string err)
            {
                _bodyWriter.AddLine($"if ({GetUnlikelyExpr(cond)})");
                _bodyWriter.AddLine("{");
                _bodyWriter.IncreaseIndent();
                EmitThrowRuntimeError(inst, err);
                _bodyWriter.DecreaseIndent();
                _bodyWriter.AddLine("}");
            }
            string ElementAt(string index) => $"(uint8_t*)({span}->__field_0) + (intptr_t)(uint32_t)({index}) * (intptr_t){elementSize}";
            switch (name)
            {
            case "get_Length":
            case "get_IsEmpty":
                if (ps.Count != 0)
                {
                    return false;
                }
                _bodyWriter.AddLine($"{ret} = ({retType})({span}->__field_1{(name == "get_IsEmpty" ? " == 0" : "")});");
                return true;
            case "get_Item":
            {
                // if ((uint)index >= (uint)_length) ThrowIndexOutOfRange(); return ref Unsafe.Add(ref _reference, (nint)(uint)index)
                if (ps.Count != 1 || !IsI4(ps[0]))
                {
                    return false;
                }
                string index = GetVariableMayCast(args[1], "int32_t");
                ThrowIf($"(uint32_t)({index}) >= (uint32_t)({span}->__field_1)", "IndexOutOfRange");
                _bodyWriter.AddLine($"{ret} = ({retType})({ElementAt(index)});");
                EmitSyncAotRoot(retVar);
                return true;
            }
            case "Slice":
            {
                // Slice(start): (uint)start > (uint)_length throws; Slice(start, length): (ulong)(uint)start + (ulong)(uint)length >
                // (ulong)(uint)_length throws (the 64-bit form of the check; the 32-bit form accepts the same arguments)
                if (retVar.type != EvalDataType.ValueType || ps.Count < 1 || ps.Count > 2 || !ps.All(IsI4))
                {
                    return false;
                }
                string start = GetVariableMayCast(args[1], "int32_t");
                string length = ps.Count == 2 ? GetVariableMayCast(args[2], "int32_t") : null;
                ThrowIf(length == null ? $"(uint32_t)({start}) > (uint32_t)({span}->__field_1)"
                    : $"(uint64_t)(uint32_t)({start}) + (uint64_t)(uint32_t)({length}) > (uint64_t)(uint32_t)({span}->__field_1)", "ArgumentOutOfRange");
                _bodyWriter.AddLine($"{ret} = {{}};");
                _bodyWriter.AddLine($"{ret}.__field_0 = (decltype({ret}.__field_0))({ElementAt(start)});");
                _bodyWriter.AddLine($"{ret}.__field_1 = {(length == null ? $"{span}->__field_1 - ({start})" : $"({length})")};");
                return true;
            }
            default:
                return false;
            }
        }

        /// <summary>The fields the Span intrinsics read: __field_0 = ref T _reference, __field_1 = int _length (the layout the
        /// Span constructor intrinsics write). Anything else keeps the calls.</summary>
        private static bool IsSpanLayout(TypeDef spanDef)
        {
            if (spanDef == null)
            {
                return false;
            }
            List<FieldDef> fields = spanDef.Fields.Where(f => !f.IsStatic).ToList();
            return fields.Count == 2 && fields[0].Name == "_reference" && fields[0].FieldType.RemovePinnedAndModifiers().ElementType == ElementType.ByRef
                && fields[1].Name == "_length" && fields[1].FieldType.RemovePinnedAndModifiers().ElementType == ElementType.I4;
        }

        /// <summary>sizeof(T) as the runtime counts it (get_first_generic_arg_size): a reference for a reference type, the
        /// value's own size for a value type (the same expression IL sizeof compiles to).</summary>
        private bool TryGetElementSizeExpr(TypeSig type, out string expr)
        {
            TypeSig t = type.RemovePinnedAndModifiers();
            if (t.ElementType == ElementType.Var || t.ElementType == ElementType.MVar || t.ElementType == ElementType.ByRef
                || t.ElementType == ElementType.TypedByRef || t.ElementType == ElementType.Void)
            {
                expr = null;
                return false;
            }
            expr = MetaUtil.IsValueType(t) ? $"sizeof({TypeNameService.GetDirectlyCppTypeName(t)})" : "sizeof(void*)";
            return true;
        }

        /// <summary>An element or byte offset argument widened like Unsafe's get_integer_offset_param.</summary>
        private static string GetIntegerOffsetExpr(TypeSig paramType, string value)
        {
            switch (paramType.RemovePinnedAndModifiers().ElementType)
            {
            case ElementType.I4:
                return $"(intptr_t)(int32_t)({value})";
            case ElementType.U4:
                return $"(intptr_t)(uint32_t)({value})";
            case ElementType.I:
                return $"(intptr_t)({value})";
            case ElementType.U:
                return $"(intptr_t)(uintptr_t)({value})";
            default:
                return null;
            }
        }

        /// <summary>ReadUnaligned / WriteUnaligned of a primitive: the bytes of that exact type (the runtime reads by reduce
        /// type and widens to the stack type; it writes the low bytes of the stack value, which is the same truncation).</summary>
        private static bool TryGetUnalignedPrimitive(TypeSig type, out string cppType)
        {
            switch (type.RemovePinnedAndModifiers().ElementType)
            {
            case ElementType.Boolean:
            case ElementType.U1: cppType = "uint8_t"; return true;
            case ElementType.I1: cppType = "int8_t"; return true;
            case ElementType.Char:
            case ElementType.U2: cppType = "uint16_t"; return true;
            case ElementType.I2: cppType = "int16_t"; return true;
            case ElementType.I4: cppType = "int32_t"; return true;
            case ElementType.U4: cppType = "uint32_t"; return true;
            case ElementType.I8: cppType = "int64_t"; return true;
            case ElementType.U8: cppType = "uint64_t"; return true;
            case ElementType.I: cppType = "intptr_t"; return true;
            case ElementType.U: cppType = "uintptr_t"; return true;
            default: cppType = null; return false;
            }
        }

        private bool TryEmitRuntimeHelpersCreateSpan(Instruction inst, MethodDef methodDef, List<EvalVariable> args, EvalVariable retVar)
        {
            if (methodDef.DeclaringType.FullName != "System.Runtime.CompilerServices.RuntimeHelpers" || methodDef.Name != "CreateSpan")
            {
                return false;
            }

            if (args.Count != 1)
            {
                throw new Exception($"Unexpected RuntimeHelpers.CreateSpan argument count: {methodDef.FullName}");
            }

            string retVarName = GetEvalVariableName(retVar);
            string fieldVarName = $"{retVarName}_field";
            string rvaDataVarName = $"{retVarName}_rva_data";
            string fieldSizeVarName = $"{retVarName}_field_size";

            _bodyWriter.AddLine($"{retVarName} = {{}};");
            _bodyWriter.AddLine($"{ConstStrings.FieldInfoPtrTypeName} {fieldVarName} = {GetEvalVariableExprWithCast(args[0], ConstStrings.FieldInfoPtrTypeName)};");
            _bodyWriter.AddLine($"{VmFunctionNames.GOTO_DECLARING_ASSIGN_OR_THROW}(const uint8_t*, {rvaDataVarName}, leanclr::codegen::get_field_rva_data({fieldVarName}), {CurMethodVar.GetFullReferenceVariableName()}, {GetCurrentIpOffset(inst)});");
            _bodyWriter.AddLine($"{VmFunctionNames.GOTO_DECLARING_ASSIGN_OR_THROW}(size_t, {fieldSizeVarName}, leanclr::codegen::get_field_size({fieldVarName}), {CurMethodVar.GetFullReferenceVariableName()}, {GetCurrentIpOffset(inst)});");
            _bodyWriter.AddLine($"{retVarName}.__field_0 = (decltype({retVarName}.__field_0)){rvaDataVarName};");
            _bodyWriter.AddLine($"{retVarName}.__field_1 = static_cast<int32_t>({fieldSizeVarName} / sizeof(*{retVarName}.__field_0));");
            return true;
        }


        private bool TryEmitCallvirIntrinsic(Instruction inst, MethodDetail methodDetail, string methodVarName, List<EvalVariable> args, EvalVariable retVar)
        {

            return false;
        }


        private MethodDef GetRedirectedCtorMethod(TypeDef stringTypeDef, MethodDef ctorMethodDef)
        {
            foreach (var redirectedMethod in stringTypeDef.Methods)
            {
                if ((redirectedMethod.Name == "CreateString" || redirectedMethod.Name == "Ctor") && redirectedMethod.ParamDefs.Count == ctorMethodDef.ParamDefs.Count)
                {
                    for (int i = 0; i < redirectedMethod.ParamDefs.Count; i++)
                    {
                        TypeSig redirectedMethodParamType = redirectedMethod.GetParam(i);
                        TypeSig ctorMethodParamType = ctorMethodDef.GetParam(i + 1); // the first parameter is the this pointer
                        if (!TypeEqualityComparer.Instance.Equals(redirectedMethodParamType, ctorMethodParamType))
                        {
                            break;
                        }
                    }
                    return redirectedMethod;
                }
            }
            throw new Exception($"impossible: the redirected ctor method of System.String not found. ctor:{ctorMethodDef.FullName}");
        }


        private bool TryRedirectNewObjIntrinsic(Instruction inst, MethodDetail methodDetail, out MethodDef redirectedMethod)
        {
            redirectedMethod = null;
            MethodDef methodDef = methodDetail.MethodDef;
            if (methodDef == null)
            {
                return false;
            }
            if (!GlobalServices.Inst.IsCoreLibraryModule(methodDef.Module))
            {
                return false;
            }
            return false;
        }

        private bool TryEmitNewobjIntrinsic(Instruction inst, MethodDetail methodDetail, Func<string> methodVarNameProvider, List<EvalVariable> args, EvalVariable retVar)
        {
            MethodDef methodDef = methodDetail.MethodDef;
            if (methodDef == null)
            {
                TypeSig declaringTypeSig = methodDetail.Method.DeclaringType.ToTypeSig();
                switch (declaringTypeSig.ElementType)
                {
                case ElementType.Array:
                {
                    EmitNewMdArrayIntrinsic(inst, methodDetail, methodVarNameProvider(), args, retVar);
                    return true;
                }
                default:
                {
                    throw new Exception("impossible: methodDef is null but the declaring type is not an array.");
                }
                }
            }

            UTF8String methodName = methodDef.Name;
            TypeDef declaringType = methodDef.DeclaringType;

            if (MetaUtil.IsDerivedFromMulticastDelegate(declaringType))
            {
                // We will handle delegate creation in a special way, so we won't treat it as a normal newobj.
                switch (methodName)
                {
                case VmFunctionNames.Ctor:
                {
                    EmitNewMulticastDelegateIntrinsic(inst, methodDetail, methodVarNameProvider(), args, retVar);
                    return true;
                }
                }
                return false;
            }
            if (!GlobalServices.Inst.IsCoreLibraryModule(methodDef.Module))
            {
                return false;
            }

            if (TryEmitSpanPointerLengthConstructor(methodDef, args, retVar))
            {
                return true;
            }

            string icallsHeader = "icalls/system_string.h";
            string icallsFuncName;
            var argsStr = CreateMethodFunctionArgsExcludedThisWithCast(methodDetail, args);
            switch (methodDef.FullName)
            {
            case "System.Void System.String::.ctor(System.Char[])":
            {
                icallsFuncName = "SystemString::newobj_char_array";
                break;
            }
            case "System.Void System.String::.ctor(System.Char[],System.Int32,System.Int32)":
            {
                icallsFuncName = "SystemString::newobj_char_array_range";
                break;
            }
            case "System.Void System.String::.ctor(System.Char*)":
            {
                icallsFuncName = "SystemString::newobj_utf16chars";
                break;
            }
            case "System.Void System.String::.ctor(System.Char*,System.Int32,System.Int32)":
            {
                icallsFuncName = "SystemString::newobj_utf16chars_range";
                break;
            }
            case "System.Void System.String::.ctor(System.SByte*)":
            {
                icallsFuncName = "SystemString::newobj_utf8chars";
                break;
            }
            case "System.Void System.String::.ctor(System.SByte*,System.Int32,System.Int32)":
            {
                icallsFuncName = "SystemString::newobj_utf8chars_range";
                break;
            }
            case "System.Void System.String::.ctor(System.SByte*,System.Int32,System.Int32,System.Text.Encoding)":
            {
                icallsFuncName = "SystemString::newobj_utf8chars_range_encoding";
                if (args.Count != 4)
                {
                    throw new Exception($"Unexpected String sbyte range encoding constructor argument count: {methodDef.FullName}");
                }
                int paramIndexOffset = methodDetail.IsStatic ? 0 : 1;
                argsStr =
                    $"{GetVariableMayCast(args[0], methodDetail.ParamsIncludeThis[paramIndexOffset].Type)}, " +
                    $"{GetVariableMayCast(args[1], methodDetail.ParamsIncludeThis[paramIndexOffset + 1].Type)}, " +
                    $"{GetVariableMayCast(args[2], methodDetail.ParamsIncludeThis[paramIndexOffset + 2].Type)}, " +
                    $"(leanclr::vm::RtObject*)({GetEvalVariableName(args[3])})";
                break;
            }
            case "System.Void System.String::.ctor(System.Char,System.Int32)":
            {
                icallsFuncName = "SystemString::newobj_char_count";
                break;
            }
            case "System.Void System.String::.ctor(System.ReadOnlySpan`1<System.Char>)":
            {
                icallsFuncName = "SystemString::newobj_readonlyspan";
                argsStr = $"*(leanclr::vm::RtReadOnlySpan<leanclr::Utf16Char>*)&{argsStr}";
                break;
            }
            default:
            {
                return false;
            }
            }
            _forwardDeclaration.AddInclude(icallsHeader);
            EmitDeclaringAssignOrThrow(inst, retVar, $"leanclr::icalls::{icallsFuncName}({argsStr})");
            EmitAssumeNotNull(retVar);
            return true;
        }

        private bool TryEmitSpanPointerLengthConstructor(MethodDef methodDef, List<EvalVariable> args, EvalVariable retVar)
        {
            if (methodDef.FullName != "System.Void System.Span`1::.ctor(System.Void*,System.Int32)" &&
                methodDef.FullName != "System.Void System.ReadOnlySpan`1::.ctor(System.Void*,System.Int32)")
            {
                return false;
            }

            if (args.Count != 2)
            {
                throw new Exception($"Unexpected Span pointer constructor argument count: {methodDef.FullName}");
            }

            string retVarName = GetEvalVariableName(retVar);
            _bodyWriter.AddLine($"{GetTypeName(retVar)} {retVarName} = {{}};");
            _bodyWriter.AddLine($"{retVarName}.__field_0 = (decltype({retVarName}.__field_0)){GetEvalVariableExprWithCast(args[0], "void*")};");
            _bodyWriter.AddLine($"{retVarName}.__field_1 = {GetEvalVariableExprWithCast(args[1], "int32_t")};");
            return true;
        }

        private void EmitCallICallOrIntrinsic(Instruction inst, MethodDetail methodDetail, RuntimeApiEntry entry, List<EvalVariable> args, EvalVariable retVar)
        {
            _forwardDeclaration.AddInclude(entry.Header);
            var argsStr = CreateMethodFunctionArgsWithCast(methodDetail, args);
            string namespaceStr = entry.MethodKind == MethodKind.ICall || entry.MethodKind == MethodKind.ICallNewObj ? "leanclr::icalls" : "leanclr::intrinsics";
            string funcFullName = $"{namespaceStr}::{entry.Func}";
            if (methodDetail.IsVoidReturn)
            {
                EmitThrowOnError(inst, $"(({methodDetail.CreateMethodFunctionTypeDefineWithoutName()}){funcFullName})({argsStr})");
            }
            else
            {
                string relaxRetTypeName = MethodGenerationUtil.GetCppTypeNameAsFieldOrArgOrLoc(methodDetail.RetType, TypeNameRelaxLevel.AbiRelaxed);
                _bodyWriter.BeginBlock();
                _bodyWriter.AddLine($"using __RetType = leanclr::core::function_return<decltype({funcFullName})>::type;");
                string wrapFuncName = MethodGenerationUtil.GetResultWrapFunctionName(relaxRetTypeName);
                EmitAssignOrThrow(inst, retVar, $"{wrapFuncName}<{relaxRetTypeName}>((({methodDetail.CreateOverrideRetTypeRelaxMethodFunctionTypeDefine("", "__RetType")}){funcFullName})({argsStr}))");
                _bodyWriter.EndBlock();
            }
        }

        private void DefineLengthsAndBoundsVar(List<EvalVariable> args, int rank)
        {
            if (args.Count == rank)
            {
                _bodyWriter.AddLine($"int32_t __lengths[] = {{{string.Join(" ,", args.Select(p => GetEvalVariableExprWithCast(p, "int32_t")))}}};");
                _bodyWriter.AddLine($"int32_t* __lowerBounds = nullptr;");
            }
            else if (args.Count == rank * 2)
            {
                List<EvalVariable> lengthArgs = args.GetRange(0, rank).ToList();
                List<EvalVariable> lowerBoundArgs = args.GetRange(rank, rank).ToList();
                _bodyWriter.AddLine($"int32_t __lengths[] = {{{string.Join(" ,", lengthArgs.Select(p => GetEvalVariableExprWithCast(p, "int32_t")))}}};");
                _bodyWriter.AddLine($"int32_t* __lowerBounds = {{{string.Join(" ,", lowerBoundArgs.Select(p => GetEvalVariableExprWithCast(p, "int32_t")))}}};");
            }
            else
            {
                throw new Exception("impossible: the number of parameters of a multidimensional array constructor should be either equal to the rank of the array or equal to the rank of the array multiplied by 2.");
            }
        }

        private void DefineIndexVar(List<EvalVariable> args, int rank)
        {
            if (args.Count == rank)
            {
                _bodyWriter.AddLine($"int32_t __indexs[] = {{{string.Join(" ,", args.Select(p => GetEvalVariableExprWithCast(p, "int32_t")))}}};");
            }
            else
            {
                throw new Exception("impossible: the number of parameters of a multidimensional array constructor should be either equal to the rank of the array or equal to the rank of the array multiplied by 2.");
            }
        }

        private void EmitNewMdArrayIntrinsic(Instruction inst, MethodDetail methodDetail, string methodVarName, List<EvalVariable> args, EvalVariable retVar)
        {
            if (args.Count < 1)
            {
                throw new Exception("impossible: the constructor of a multidimensional array should always have at least 1 parameters.");
            }
            ArraySig declaringTypeSig = (ArraySig)methodDetail.Method.DeclaringType.ToTypeSig().RemoveModifiers();
            ParamDetail[] paramsIncludeThis = methodDetail.ParamsIncludeThis;
            int rank = (int)declaringTypeSig.Rank;
            _bodyWriter.AddLine($"{GetTypeName(retVar)} {GetEvalVariableName(retVar)};");
            _bodyWriter.AddLine("{");
            _bodyWriter.IncreaseIndent();
            DefineLengthsAndBoundsVar(args, rank);
            EmitAssignOrThrow(inst, retVar, $"{VmFunctionNames.NewMdArrayFromArrayKlass}({GetParentFromFullReferenceMethodVariable(methodVarName)}, __lengths, __lowerBounds, \"{_method.UniqueName}\")");
            _bodyWriter.DecreaseIndent();
            _bodyWriter.AddLine("}");
            EmitAssumeNotNull(retVar);
        }

        private void EmitCallMdArrayGetIntrinsic(Instruction inst, MethodDetail methodDetail, string methodVarName, List<EvalVariable> args, EvalVariable retVar)
        {
            if (args.Count < 2)
            {
                throw new Exception("impossible: the constructor of a multidimensional array should always have at least 2 parameters.");
            }
            ArraySig declaringTypeSig = (ArraySig)methodDetail.Method.DeclaringType.ToTypeSig().RemoveModifiers();
            ParamDetail[] paramsIncludeThis = methodDetail.ParamsIncludeThis;
            int rank = (int)declaringTypeSig.Rank;
            EvalVariable arrVar = args[0];
            EmitCheckNotNull(inst, arrVar);

            //_bodyWriter.AddLine($"{GetTypeName(retVar)} {GetEvalVariableName(retVar)};");
            _bodyWriter.AddLine("{");
            _bodyWriter.IncreaseIndent();
            DefineIndexVar(args.GetRange(1, args.Count - 1), rank);
            string globalIndexVarName = "__globalIndex";
            _bodyWriter.AddLine($"{VmFunctionNames.GOTO_DECLARING_ASSIGN_OR_THROW}(int32_t, {globalIndexVarName}, {VmFunctionNames.GetMdArrayGlobalIndex}({GetVariableMayCast(arrVar, ConstStrings.ArrayPtrTypeName)}, __indexs), {CurMethodVar.GetFullReferenceVariableName()}, {GetCurrentIpOffset(inst)});");
            ITypeDefOrRef elementType = methodDetail.RetType.ToTypeDefOrRef();
            string elementTypeName = GetExactTypeName(elementType);
            string loadElementDataExpr = $"{VmFunctionNames.GetArrayElementDataAt}<{elementTypeName}>({GetVariableMayCast(arrVar, ConstStrings.ArrayPtrTypeName)}, {globalIndexVarName})";
            _bodyWriter.AddLine($"{GetEvalVariableName(retVar)} = {MayFoldCast(elementTypeName, GetTypeName(retVar), loadElementDataExpr)};");
            _bodyWriter.DecreaseIndent();
            _bodyWriter.AddLine("}");
        }

        private void EmitCallMdArraySetIntrinsic(Instruction inst, MethodDetail methodDetail, string methodVarName, List<EvalVariable> args, EvalVariable retVar)
        {
            if (args.Count < 3)
            {
                throw new Exception("impossible: the constructor of a multidimensional array should always have at least 3 parameters.");
            }
            ArraySig declaringTypeSig = (ArraySig)methodDetail.Method.DeclaringType.ToTypeSig().RemoveModifiers();
            ParamDetail[] paramsIncludeThis = methodDetail.ParamsIncludeThis;
            int rank = (int)declaringTypeSig.Rank;
            EvalVariable arrVar = args[0];
            EvalVariable valueVar = args.Last();
            EmitCheckNotNull(inst, arrVar);

            //_bodyWriter.AddLine($"{GetTypeName(retVar)} {GetEvalVariableName(retVar)};");
            _bodyWriter.AddLine("{");
            _bodyWriter.IncreaseIndent();
            DefineIndexVar(args.GetRange(1, args.Count - 2), rank);
            string globalIndexVarName = "__globalIndex";
            _bodyWriter.AddLine($"{VmFunctionNames.GOTO_DECLARING_ASSIGN_OR_THROW}(int32_t, {globalIndexVarName}, {VmFunctionNames.GetMdArrayGlobalIndex}({GetVariableMayCast(arrVar, ConstStrings.ArrayPtrTypeName)}, __indexs), {CurMethodVar.GetFullReferenceVariableName()}, {GetCurrentIpOffset(inst)});");
            ITypeDefOrRef elementType = paramsIncludeThis.Last().Type.ToTypeDefOrRef();
            string elementTypeName = GetExactTypeName(elementType);

            string valueVarName = GetEvalVariableName(valueVar);
            _bodyWriter.AddLine($"{VmFunctionNames.SetArrayElementDataAt}<{elementTypeName}>({GetVariableMayCast(arrVar, ConstStrings.ArrayPtrTypeName)}, {globalIndexVarName}, {GetEvalVariableExprWithCast(valueVar, elementTypeName)});");
            _bodyWriter.DecreaseIndent();
            _bodyWriter.AddLine("}");
        }

        private void EmitCallMdArrayAddressIntrinsic(Instruction inst, MethodDetail methodDetail, string methodVarName, List<EvalVariable> args, EvalVariable retVar)
        {
            if (args.Count < 2)
            {
                throw new Exception("impossible: the constructor of a multidimensional array should always have at least 2 parameters.");
            }
            ArraySig declaringTypeSig = (ArraySig)methodDetail.Method.DeclaringType.ToTypeSig().RemoveModifiers();
            ParamDetail[] paramsIncludeThis = methodDetail.ParamsIncludeThis;
            int rank = (int)declaringTypeSig.Rank;
            EvalVariable arrVar = args[0];
            EmitCheckNotNull(inst, arrVar);

            //_bodyWriter.AddLine($"{GetTypeName(retVar)} {GetEvalVariableName(retVar)};");
            _bodyWriter.AddLine("{");
            _bodyWriter.IncreaseIndent();
            DefineIndexVar(args.GetRange(1, args.Count - 1), rank);
            string globalIndexVarName = "__globalIndex";
            _bodyWriter.AddLine($"{VmFunctionNames.GOTO_DECLARING_ASSIGN_OR_THROW}(int32_t, {globalIndexVarName}, {VmFunctionNames.GetMdArrayGlobalIndex}({GetVariableMayCast(arrVar, ConstStrings.ArrayPtrTypeName)}, __indexs), {CurMethodVar.GetFullReferenceVariableName()}, {GetCurrentIpOffset(inst)});");
            ITypeDefOrRef elementType = methodDetail.RetType.Next.ToTypeDefOrRef();
            string elementTypeName = GetExactTypeName(elementType);
            string loadElementDataExpr = $"{VmFunctionNames.GetArrayElementAddress}<{elementTypeName}>({GetVariableMayCast(arrVar, ConstStrings.ArrayPtrTypeName)}, {globalIndexVarName})";
            _bodyWriter.AddLine($"{GetEvalVariableName(retVar)} = {MayFoldCast($"{elementTypeName}*", GetTypeName(retVar), loadElementDataExpr)};");
            _bodyWriter.DecreaseIndent();
            _bodyWriter.AddLine("}");
        }

        private void EmitNewMulticastDelegateIntrinsic(Instruction inst, MethodDetail methodDetail, string methodVarName, List<EvalVariable> args, EvalVariable retVar)
        {
            if (args.Count != 2)
            {
                throw new Exception("impossible: the constructor of a delegate should always have 2 parameters.");
            }
            EmitDeclaringAssignOrThrow(inst, retVar, $"{VmFunctionNames.NewDelegate}({GetParentFromFullReferenceMethodVariable(methodVarName)}, ({ConstStrings.ObjectPtrTypeName}){GetEvalVariableName(args[0])}, ({ConstStrings.MethodInfoPtrTypeName}){GetEvalVariableName(args[1])})");
            EmitAssumeNotNull(retVar);
        }
    }
}
