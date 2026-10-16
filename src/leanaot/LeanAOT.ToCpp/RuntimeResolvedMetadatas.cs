
using dnlib.DotNet;
using System.Diagnostics;

namespace LeanAOT.ToCpp
{
    class SubRuntimeResolvedVariable
    {
        public string Key { get; }

        public string VariableName { get; }

        public string FullReferenceVariableName { get; }

        public string TypeName { get; }

        public string InitExpression { get; }

        public SubRuntimeResolvedVariable(string key, string variableName, string fullReferenceVariableName, string typeName, string initExpression)
        {
            Key = key;
            VariableName = variableName;
            FullReferenceVariableName = fullReferenceVariableName;
            TypeName = typeName;
            InitExpression = initExpression;
        }
    }

    class RuntimeResolvedVariable
    {

        public readonly RuntimeResolvedMetadatas owner;
        public readonly int varId;
        public readonly uint token;

        public readonly object value;

        public readonly object detailValue;

        private readonly Dictionary<string, SubRuntimeResolvedVariable> subVariables = new Dictionary<string, SubRuntimeResolvedVariable>();

        /// <summary>Non-null: a definition of another module, resolved by that module's name and its own token (not part of
        /// the method's token table).</summary>
        public readonly string foreignModule;

        /// <summary>Non-null: the instantiation's own RtMethodInfo, read from this global (see MethodDetail.SelfSlotName).</summary>
        public readonly string selfReference;

        /// <summary>Non-null: computed at metadata init from other variables of the method (e.g. the implementation a
        /// constrained call resolves to, which the runtime looks up the same way the interpreter does).</summary>
        public readonly string derivedInit;

        public RuntimeResolvedVariable(RuntimeResolvedMetadatas owner, int varId, uint token, object value, object detailValue, string foreignModule = null, string selfReference = null, string derivedInit = null)
        {
            this.derivedInit = derivedInit;
            this.owner = owner;
            this.varId = varId;
            this.token = token;
            this.value = value;
            this.detailValue = detailValue;
            this.foreignModule = foreignModule;
            this.selfReference = selfReference;
        }

        public object Value => value;

        public bool IsTokenResolved => foreignModule == null && selfReference == null && derivedInit == null;

        public string GetVariableName()
        {
            return $"__r{varId}";
        }

        public string GetVariableTypeName()
        {
            if (value is IMethod method && method.IsMethod)
            {
                return "const leanclr::metadata::RtMethodInfo*";
            }
            else if (value is IField field && field.IsField)
            {
                return "leanclr::metadata::RtFieldInfo*";
            }
            else if (value is ITypeDefOrRef type && type.IsType)
            {
                return "leanclr::metadata::RtClass*";
            }
            else if (value is string)
            {
                return "leanclr::vm::RtString*";
            }
            else
            {
                throw new Exception($"Unsupported value type for runtime resolved variable: {value}");
            }
        }

        public IReadOnlyList<SubRuntimeResolvedVariable> GetSubVariables()
        {
            return subVariables.Values.ToList();
        }

        public string GetFullReferenceVariableName()
        {
            if (selfReference != null)
            {
                return selfReference;
            }
            if (foreignModule != null || derivedInit != null)
            {
                return $"{owner.GetSubResolveMetadatasVariableName()}.{GetVariableName()}";
            }
            return $"{owner.GetResolveMetadatasVariableName()}.{GetVariableName()}";
        }

        public string GetForeignInitExpression()
        {
            return $"({GetVariableTypeName()}){ConstStrings.CodegenNamespace}::resolve_metadata_token_in_module(\"{foreignModule}\", 0x{token:X8})";
        }

        //public string GetFullReferenceVariableNameAsMethodInfo()
        //{
        //    return $"((const leanclr::metadata::RtMethodInfo*){owner.GetResolveMetadatasPtrVariableName()}->{GetVariableName()})";
        //}

        //public string GetFullReferenceVariableNameAsClass()
        //{
        //    return $"((const leanclr::metadata::RtClass*){owner.GetResolveMetadatasPtrVariableName()}->{GetVariableName()})";
        //}

        public SubRuntimeResolvedVariable GetFieldStaticDataSubVariable()
        {
            FieldDetail fieldDetail = (FieldDetail)detailValue;
            Debug.Assert(fieldDetail.FieldBase.IsStatic);
            string key = "static_data";
            if (subVariables.TryGetValue(key, out var subVariable))
            {
                return subVariable;
            }
            string name = $"{GetVariableName()}_{key}";
            string fullReferenceVariableName = $"{owner.GetSubResolveMetadatasVariableName()}.{name}";
            string fieldType = $"{fieldDetail.Parent.StaticTypeName}*";
            subVariable = new SubRuntimeResolvedVariable(key, name, fullReferenceVariableName, fieldType, $"({fieldType})({GetFullReferenceVariableName()})->parent->{ConstStrings.KlassFieldNameStaticFieldsData}");
            subVariables.Add(key, subVariable);
            return subVariable;
        }

        public string GetFullReferenceVariableNameBeforeInit()
        {
            return GetFullReferenceVariableName();
        }

        // public string ResolveCode()
        // {
        //     if (value is string str)
        //     {
        //         return $"leanclr::codegen::resolve_string_literal({ModuleGenerationUtil.GetModuleGlobalVariableName(owner.Module)}, 0x{token:X8})";
        //     }
        //     else
        //     {
        //         return $"({GetVariableTypeName()})leanclr::codegen::resolve_metadata_token({ModuleGenerationUtil.GetModuleGlobalVariableName(owner.Module)}, 0x{token:X8}, nullptr)";
        //     }
        // }
    }

    class UserString
    {
        public readonly RuntimeResolvedVariable variable;

        public UserString(RuntimeResolvedVariable variable)
        {
            this.variable = variable;
        }
    }

    /// <summary>
    /// The metadata a generated method resolves once, on its first call. Normally every entry is a token of the method's
    /// module, resolved with resolve_metadata_tokens. In an instantiation of a generic definition the operands are the
    /// definition's tokens (the ones in its IL), resolved in the definition's module with the instantiation's RtMethodInfo
    /// as generic context, exactly as the interpreter's transformer does (resolve_generic_method_metadata_tokens), so
    /// <see cref="MapOperand"/> records which inflated operand came from which token. Definitions found by the
    /// generator itself rather than named by an operand (the implementing method of a constrained call, a redirected
    /// constructor) are resolved by module name and their own token.
    /// </summary>
    class RuntimeResolvedMetadatas
    {
        private readonly MethodDetail _method;
        private readonly Dictionary<string, UserString> _userStrings = new Dictionary<string, UserString>();
        private readonly Dictionary<uint, RuntimeResolvedVariable> _tokenVariables = new Dictionary<uint, RuntimeResolvedVariable>();
        private readonly Dictionary<string, RuntimeResolvedVariable> _foreignVariables = new Dictionary<string, RuntimeResolvedVariable>(StringComparer.Ordinal);
        private readonly Dictionary<object, uint> _operandTokens = new Dictionary<object, uint>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, RuntimeResolvedVariable> _derivedByValue = new Dictionary<object, RuntimeResolvedVariable>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, RuntimeResolvedVariable> _derivedByKey = new Dictionary<string, RuntimeResolvedVariable>(StringComparer.Ordinal);
        private readonly List<string> _postInitStatements = new List<string>();
        private int _lastResolvedVariableId;
        private readonly List<RuntimeResolvedVariable> _resolvedVariables = new List<RuntimeResolvedVariable>();
        private RuntimeResolvedVariable _selfVariable;

        public RuntimeResolvedMetadatas(MethodDetail method)
        {
            _method = method;
        }

        public ModuleDef Module => _method.Module;

        public bool IsGeneric => _method.IsGeneric;

        public bool IsEmpty()
        {
            return _lastResolvedVariableId == 0 && _postInitStatements.Count == 0;
        }

        public IReadOnlyList<RuntimeResolvedVariable> ResolvedVariables => _resolvedVariables;

        public IReadOnlyList<RuntimeResolvedVariable> TokenResolvedVariables => _resolvedVariables.Where(v => v.IsTokenResolved).ToList();

        public IReadOnlyList<RuntimeResolvedVariable> ForeignVariables => _resolvedVariables.Where(v => v.foreignModule != null).ToList();

        /// <summary>In creation order: an init expression only names variables created before it.</summary>
        public IReadOnlyList<RuntimeResolvedVariable> DerivedVariables => _resolvedVariables.Where(v => v.derivedInit != null).ToList();

        /// <summary>
        /// A variable computed at metadata init by <paramref name="initExpression"/> (from variables already created). Later
        /// lookups of the same <paramref name="value"/> object (GetMethodVariable etc.) return it, so a member the generator
        /// derived itself is resolved by the runtime, not by token.
        /// </summary>
        public RuntimeResolvedVariable GetDerivedVariable(string key, IMDTokenProvider value, object detailValue, Func<string> initExpression)
        {
            if (!_derivedByKey.TryGetValue(key, out var variable))
            {
                variable = new RuntimeResolvedVariable(this, ++_lastResolvedVariableId, 0, value, detailValue, derivedInit: initExpression());
                _resolvedVariables.Add(variable);
                _derivedByKey.Add(key, variable);
            }
            _derivedByValue[value] = variable;
            return variable;
        }

        public IReadOnlyList<string> PostInitStatements => _postInitStatements;

        public void AddPostInitStatement(string statement)
        {
            if (!_postInitStatements.Contains(statement))
            {
                _postInitStatements.Add(statement);
            }
        }

        /// <summary>Generic definitions: <paramref name="inflated"/> is what the operand with <paramref name="originalToken"/>
        /// became in this instantiation.</summary>
        public void MapOperand(object inflated, uint originalToken)
        {
            if (inflated != null && originalToken != 0)
            {
                _operandTokens[inflated] = originalToken;
            }
        }

        public RuntimeResolvedVariable GetSelfVariable()
        {
            return _selfVariable ??= new RuntimeResolvedVariable(this, 0, 0, _method.Method, _method, selfReference: _method.SelfSlotName);
        }

        private RuntimeResolvedVariable NewResolvedVariable(uint token, object value, object detailValue)
        {
            var variable = new RuntimeResolvedVariable(this, ++_lastResolvedVariableId, token, value, detailValue);
            _resolvedVariables.Add(variable);
            return variable;
        }

        private RuntimeResolvedVariable GetVariable(IMDTokenProvider value, object detailValue)
        {
            if (_derivedByValue.TryGetValue(value, out var derived))
            {
                return derived;
            }
            uint token;
            if (IsGeneric)
            {
                if (!_operandTokens.TryGetValue(value, out token))
                {
                    return GetForeignVariable(value, detailValue);
                }
            }
            else
            {
                if (value is IMemberDef def && def.Module != _method.Module)
                {
                    return GetForeignVariable(value, detailValue);
                }
                token = value.MDToken.ToUInt32();
                if (token == 0)
                {
                    throw new NotSupportedException($"{value} has no token in {_method.Module.Name}; cannot resolve it from {_method.FullName}");
                }
            }
            if (!_tokenVariables.TryGetValue(token, out var variable))
            {
                variable = NewResolvedVariable(token, value, detailValue);
                _tokenVariables.Add(token, variable);
            }
            return variable;
        }

        private RuntimeResolvedVariable GetForeignVariable(IMDTokenProvider value, object detailValue)
        {
            bool isOpenGeneric = value switch
            {
                MethodDef md => md.HasGenericParameters || md.DeclaringType.HasGenericParameters,
                FieldDef fd => fd.DeclaringType.HasGenericParameters,
                TypeDef td => td.HasGenericParameters,
                _ => true,
            };
            if (isOpenGeneric || value is not IMemberDef def)
            {
                throw new NotSupportedException($"{value} is not an operand of {_method.FullName}; generated code cannot resolve it");
            }
            string module = def.Module.Assembly.Name;
            uint token = value.MDToken.ToUInt32();
            string key = $"{module}:{token:X8}";
            if (!_foreignVariables.TryGetValue(key, out var variable))
            {
                variable = new RuntimeResolvedVariable(this, ++_lastResolvedVariableId, token, value, detailValue, foreignModule: module);
                _resolvedVariables.Add(variable);
                _foreignVariables.Add(key, variable);
            }
            return variable;
        }

        public string GetResolveMetadataStructName()
        {
            return $"{_method.UniqueName}__ResolvedMetadatas{(_method.IsGeneric ? "_Generic" : "")}";
        }

        public string GetResolveMetadatasVariableName()
        {
            return $"{_method.UniqueName}__resolvedMetadatas";
        }

        public string getSubResolveMetadataStructName()
        {
            return $"{_method.UniqueName}__SubResolvedMetadatas{(_method.IsGeneric ? "_Generic" : "")}";
        }

        public string GetSubResolveMetadatasVariableName()
        {
            return $"{_method.UniqueName}__subResolvedMetadatas";
        }

        public string GetResolveMetadatasInitedVariableName()
        {
            return $"{_method.UniqueName}__resolvedMetadatas_inited";
        }

        public string GetResolveMetadatasTokensVariableName()
        {
            return $"{_method.UniqueName}__resolvedMetadatas_tokens";
        }

        public RuntimeResolvedVariable GetUserStringVariable(string value, MDToken token)
        {
            if (!_userStrings.TryGetValue(value, out var userString))
            {
                var variable = NewResolvedVariable(token.ToUInt32(), value, null); // User string token base
                userString = new UserString(variable);
                _userStrings.Add(value, userString);
            }
            return userString.variable;
        }

        public RuntimeResolvedVariable GetMethodVariable(IMethod method, MethodDetail methodDetail)
        {
            return GetVariable(method, methodDetail);
        }

        public RuntimeResolvedVariable GetTypeVariable(ITypeDefOrRef type, TypeDetail typeDetail)
        {
            return GetVariable(type, typeDetail);
        }

        public RuntimeResolvedVariable GetFieldVariable(IField field, FieldDetail fieldDetail)
        {
            return GetVariable(field, fieldDetail);
        }
    }
}