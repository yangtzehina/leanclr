using dnlib.DotNet;
using System.Text;

namespace LeanAOT.Core
{
    /// <summary>
    /// A closed instantiation of a generic method or of a method on a generic type: the definition plus concrete class
    /// and method type arguments, in one canonical dnlib form (MemberRef on the instantiated type, MethodSpec for method
    /// arguments) so that every place that meets the same instantiation generates and names it the same way.
    ///
    /// Two keys:
    ///   <see cref="RidKey"/>  what the runtime computes from its own type data when it inflates a method
    ///                         (metadata/aot_module.cpp, AotModule::format_generic_method_key): assembly names plus
    ///                         TypeDef / MethodDef row ids. Valid only together with the images the code was generated
    ///                         from, which the MVID binding of the home module guarantees.
    ///   <see cref="NameKey"/> the same instantiation by names and signatures only, for lists kept in the repository
    ///                         (runtime/aot/generics-hot.txt): it survives rebuilds of the DLLs, row ids do not.
    /// </summary>
    public sealed class GenericInstantiation
    {
        public const string CoreLibName = "System.Private.CoreLib";

        public MethodDef Def { get; }
        public IList<TypeSig> ClassArgs { get; }
        public IList<TypeSig> MethodArgs { get; }
        public IMethod Method { get; }
        public string RidKey { get; }

        private GenericInstantiation(MethodDef def, IList<TypeSig> classArgs, IList<TypeSig> methodArgs, IMethod method, string ridKey)
        {
            Def = def;
            ClassArgs = classArgs;
            MethodArgs = methodArgs;
            Method = method;
            RidKey = ridKey;
        }

        public override string ToString() => Method.FullName;

        public GenericArgumentContext GAC => new GenericArgumentContext(ClassArgs.Count > 0 ? ClassArgs : null, MethodArgs.Count > 0 ? MethodArgs : null);

        public static bool IsGenericDefinition(MethodDef def) => def != null && (def.HasGenericParameters || def.DeclaringType.HasGenericParameters);

        /// <summary>The instantiation an inflated (caller-side) method reference names, or null when it is not a closed
        /// generic instantiation (or names something the runtime keys cannot express).</summary>
        public static GenericInstantiation TryFrom(IMethod method)
        {
            if (method == null || !method.IsMethod)
                return null;
            MethodDef def;
            try
            {
                def = method.ResolveMethodDef();
            }
            catch (Exception)
            {
                return null;
            }
            if (!IsGenericDefinition(def))
                return null;
            GenericArgumentContext gac;
            try
            {
                gac = MetaUtil.GetMethodGenericArgumentContext(method);
            }
            catch (NotSupportedException)
            {
                return null;
            }
            return TryCreate(def, gac.typeArgsStack, gac.methodArgsStack);
        }

        public static GenericInstantiation TryCreate(MethodDef def, IList<TypeSig> classArgs, IList<TypeSig> methodArgs)
        {
            classArgs = (classArgs ?? Array.Empty<TypeSig>()).Select(a => a?.RemovePinnedAndModifiers()).ToList();
            methodArgs = (methodArgs ?? Array.Empty<TypeSig>()).Select(a => a?.RemovePinnedAndModifiers()).ToList();
            if (classArgs.Count != def.DeclaringType.GenericParameters.Count || methodArgs.Count != def.GenericParameters.Count)
                return null;
            if (classArgs.Concat(methodArgs).Any(a => a == null || a.ContainsGenericParameter || a.ElementType == ElementType.ByRef))
                return null;
            string key = TryRidKey(def, classArgs, methodArgs);
            if (key == null)
                return null;

            IMethodDefOrRef baseMethod = def;
            if (classArgs.Count > 0)
            {
                TypeDef declaring = def.DeclaringType;
                ClassOrValueTypeSig open = declaring.IsValueType ? new ValueTypeSig(declaring) : new ClassSig(declaring);
                // the definition's own signature: in a MemberRef on a generic type, !n means the type's parameters
                baseMethod = new MemberRefUser(def.Module, def.Name, def.MethodSig, new TypeSpecUser(new GenericInstSig(open, classArgs)));
            }
            IMethod method = methodArgs.Count > 0 ? new MethodSpecUser(baseMethod, new GenericInstMethodSig(methodArgs)) : baseMethod;
            return new GenericInstantiation(def, classArgs, methodArgs, method, key);
        }

        // ---- RidKey ------------------------------------------------------------------------------------------------------

        private static string TryRidKey(MethodDef def, IList<TypeSig> classArgs, IList<TypeSig> methodArgs)
        {
            var sb = new StringBuilder();
            sb.Append(def.Module.Assembly.Name.String).Append('!').Append(def.Rid.ToString("x")).Append('<');
            for (int i = 0; i < classArgs.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                if (!AppendTypeRidKey(sb, classArgs[i]))
                    return null;
            }
            sb.Append(';');
            for (int i = 0; i < methodArgs.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                if (!AppendTypeRidKey(sb, methodArgs[i]))
                    return null;
            }
            sb.Append('>');
            return sb.ToString();
        }

        /// <summary>The runtime key of one type (as it appears inside <see cref="RidKey"/>), or null.</summary>
        public static string TypeKey(TypeSig sig)
        {
            var sb = new StringBuilder();
            return sig != null && AppendTypeRidKey(sb, sig) ? sb.ToString() : null;
        }

        private static readonly Dictionary<string, ElementType> s_primitiveNames = new Dictionary<string, ElementType>(StringComparer.Ordinal)
        {
            ["Void"] = ElementType.Void, ["Boolean"] = ElementType.Boolean, ["Char"] = ElementType.Char, ["SByte"] = ElementType.I1,
            ["Byte"] = ElementType.U1, ["Int16"] = ElementType.I2, ["UInt16"] = ElementType.U2, ["Int32"] = ElementType.I4,
            ["UInt32"] = ElementType.U4, ["Int64"] = ElementType.I8, ["UInt64"] = ElementType.U8, ["Single"] = ElementType.R4,
            ["Double"] = ElementType.R8, ["String"] = ElementType.String, ["TypedReference"] = ElementType.TypedByRef,
            ["IntPtr"] = ElementType.I, ["UIntPtr"] = ElementType.U, ["Object"] = ElementType.Object,
        };

        /// <summary>The runtime gives every CoreLib TypeDef with one of these names a primitive element type
        /// (RtModuleDef::get_type_def_by_val_typesig), whatever the signature that named it.</summary>
        public static bool TryGetPrimitive(TypeDef td, out ElementType elementType)
        {
            elementType = ElementType.End;
            return td != null && td.Module?.Assembly?.Name == CoreLibName && s_primitiveNames.TryGetValue(td.Name, out elementType);
        }

        public static TypeDef ResolveTypeDef(ITypeDefOrRef type)
        {
            try
            {
                return type?.ResolveTypeDef();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool AppendTypeRidKey(StringBuilder sb, TypeSig sig)
        {
            sig = sig.RemovePinnedAndModifiers();
            switch (sig.ElementType)
            {
            case ElementType.Void:
            case ElementType.Boolean:
            case ElementType.Char:
            case ElementType.I1:
            case ElementType.U1:
            case ElementType.I2:
            case ElementType.U2:
            case ElementType.I4:
            case ElementType.U4:
            case ElementType.I8:
            case ElementType.U8:
            case ElementType.R4:
            case ElementType.R8:
            case ElementType.String:
            case ElementType.TypedByRef:
            case ElementType.I:
            case ElementType.U:
            case ElementType.Object:
                sb.Append('E').Append(((byte)sig.ElementType).ToString("x2"));
                return true;
            case ElementType.Class:
            case ElementType.ValueType:
            {
                TypeDef td = ResolveTypeDef(((TypeDefOrRefSig)sig).TypeDefOrRef);
                if (td == null)
                    return false;
                if (TryGetPrimitive(td, out ElementType et))
                {
                    sb.Append('E').Append(((byte)et).ToString("x2"));
                    return true;
                }
                sb.Append('T').Append(td.Module.Assembly.Name.String).Append(':').Append(td.Rid.ToString("x"));
                return true;
            }
            case ElementType.GenericInst:
            {
                var gi = (GenericInstSig)sig;
                TypeDef td = ResolveTypeDef(gi.GenericType.TypeDefOrRef);
                if (td == null)
                    return false;
                sb.Append('G').Append('T').Append(td.Module.Assembly.Name.String).Append(':').Append(td.Rid.ToString("x")).Append('<');
                for (int i = 0; i < gi.GenericArguments.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    if (!AppendTypeRidKey(sb, gi.GenericArguments[i]))
                        return false;
                }
                sb.Append('>');
                return true;
            }
            case ElementType.SZArray:
                sb.Append('Z');
                return AppendTypeRidKey(sb, sig.Next);
            case ElementType.Array:
                sb.Append('A').Append(((ArraySig)sig).Rank);
                return AppendTypeRidKey(sb, sig.Next);
            case ElementType.Ptr:
                sb.Append('P');
                return AppendTypeRidKey(sb, sig.Next);
            case ElementType.ByRef:
                sb.Append('R');
                return AppendTypeRidKey(sb, sig.Next);
            default:
                return false;
            }
        }

        /// <summary>Assemblies the instantiation's code depends on: the definition's and every type argument's.</summary>
        public IEnumerable<string> InvolvedAssemblies()
        {
            var set = new SortedSet<string>(StringComparer.Ordinal) { Def.Module.Assembly.Name };
            foreach (TypeSig a in ClassArgs.Concat(MethodArgs))
                CollectAssemblies(a, set);
            return set;
        }

        private static void CollectAssemblies(TypeSig sig, ISet<string> set)
        {
            sig = sig.RemovePinnedAndModifiers();
            switch (sig)
            {
            case GenericInstSig gi:
                CollectAssemblies(gi.GenericType, set);
                foreach (TypeSig a in gi.GenericArguments)
                    CollectAssemblies(a, set);
                return;
            case TypeDefOrRefSig tdr:
            {
                TypeDef td = ResolveTypeDef(tdr.TypeDefOrRef);
                if (td != null)
                    set.Add(td.Module.Assembly.Name);
                return;
            }
            case NonLeafSig nl:
                CollectAssemblies(nl.Next, set);
                return;
            }
        }

        /// <summary>Deepest nesting of generic instantiations / arrays in the type arguments (guards the closure walk
        /// against instantiations that grow without end, e.g. M&lt;T&gt; calling M&lt;List&lt;T&gt;&gt;).</summary>
        public int Depth() => ClassArgs.Concat(MethodArgs).Select(TypeDepth).DefaultIfEmpty(0).Max();

        private static int TypeDepth(TypeSig sig)
        {
            sig = sig.RemovePinnedAndModifiers();
            return sig switch
            {
                GenericInstSig gi => 1 + gi.GenericArguments.Select(TypeDepth).DefaultIfEmpty(0).Max(),
                NonLeafSig nl => 1 + TypeDepth(nl.Next),
                _ => 0,
            };
        }

        /// <summary>Parses a <see cref="RidKey"/> (as the runtime's profile output prints it) against the DLLs it was
        /// computed from; null when a row id does not resolve.</summary>
        public static GenericInstantiation ParseRidKey(string key, AssemblyCache cache)
        {
            int pos = 0;
            string asm = ReadUntil(key, ref pos, '!');
            if (asm == null)
                return null;
            pos++;
            string ridText = ReadUntil(key, ref pos, '<');
            if (ridText == null || !uint.TryParse(ridText, System.Globalization.NumberStyles.HexNumber, null, out uint rid))
                return null;
            ModuleDef mod = TryLoad(cache, asm);
            if (mod is not ModuleDefMD md || md.ResolveMethod(rid) is not MethodDef def)
                return null;
            pos++;
            var classArgs = new List<TypeSig>();
            var methodArgs = new List<TypeSig>();
            var target = classArgs;
            while (pos < key.Length && key[pos] != '>')
            {
                if (key[pos] == ';')
                {
                    target = methodArgs;
                    pos++;
                    continue;
                }
                if (key[pos] == ',')
                {
                    pos++;
                    continue;
                }
                TypeSig t = ParseRidType(key, ref pos, cache);
                if (t == null)
                    return null;
                target.Add(t);
            }
            return TryCreate(def, classArgs, methodArgs);
        }

        private static string ReadUntil(string s, ref int pos, char end)
        {
            int start = pos;
            while (pos < s.Length && s[pos] != end)
                pos++;
            return pos < s.Length ? s.Substring(start, pos - start) : null;
        }

        private static TypeSig ParseRidType(string s, ref int pos, AssemblyCache cache)
        {
            if (pos >= s.Length)
                return null;
            char c = s[pos++];
            switch (c)
            {
            case 'E':
            {
                if (pos + 2 > s.Length)
                    return null;
                var et = (ElementType)Convert.ToByte(s.Substring(pos, 2), 16);
                pos += 2;
                ModuleDef corlib = TryLoad(cache, CoreLibName);
                TypeDef td = corlib == null ? null : s_primitiveNames.Where(kv => kv.Value == et).Select(kv => corlib.Find("System." + kv.Key, false)).FirstOrDefault(t => t != null);
                return td == null ? null : corlib.CorLibTypes.GetCorLibTypeSig(td);
            }
            case 'T':
            {
                string asm = ReadUntil(s, ref pos, ':');
                if (asm == null)
                    return null;
                pos++;
                int start = pos;
                while (pos < s.Length && Uri.IsHexDigit(s[pos]))
                    pos++;
                if (!uint.TryParse(s.Substring(start, pos - start), System.Globalization.NumberStyles.HexNumber, null, out uint rid))
                    return null;
                if (TryLoad(cache, asm) is not ModuleDefMD md || md.ResolveTypeDef(rid) is not TypeDef td)
                    return null;
                return td.IsValueType ? new ValueTypeSig(td) : new ClassSig(td);
            }
            case 'G':
            {
                if (ParseRidType(s, ref pos, cache) is not ClassOrValueTypeSig generic || pos >= s.Length || s[pos] != '<')
                    return null;
                pos++;
                var args = new List<TypeSig>();
                while (pos < s.Length && s[pos] != '>')
                {
                    if (s[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    TypeSig a = ParseRidType(s, ref pos, cache);
                    if (a == null)
                        return null;
                    args.Add(a);
                }
                pos++;
                return new GenericInstSig(generic, args);
            }
            case 'Z':
                return ParseRidType(s, ref pos, cache) is TypeSig z ? new SZArraySig(z) : null;
            case 'A':
            {
                int start = pos;
                while (pos < s.Length && char.IsDigit(s[pos]))
                    pos++;
                uint rank = uint.Parse(s.Substring(start, pos - start));
                return ParseRidType(s, ref pos, cache) is TypeSig a ? new ArraySig(a, rank) : null;
            }
            case 'P':
                return ParseRidType(s, ref pos, cache) is TypeSig p ? new PtrSig(p) : null;
            case 'R':
                return ParseRidType(s, ref pos, cache) is TypeSig r ? new ByRefSig(r) : null;
            default:
                return null;
            }
        }

        // ---- NameKey -----------------------------------------------------------------------------------------------------
        //   <definition assembly>|<MethodDef.FullName>|<class args>|<method args>
        // type argument: [assembly]Namespace.Name (nested: Outer/Inner), then <arg,...> for generic instances and [] [,] * &
        // suffixes; \ escapes the characters this grammar uses when they occur in a name.

        private const string NameSpecials = "\\<>,[]*&|";

        public string NameKey
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append(Def.Module.Assembly.Name.String).Append('|').Append(Def.FullName).Append('|');
                AppendNameList(sb, ClassArgs);
                sb.Append('|');
                AppendNameList(sb, MethodArgs);
                return sb.ToString();
            }
        }

        private static void AppendNameList(StringBuilder sb, IList<TypeSig> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                AppendTypeName(sb, args[i]);
            }
        }

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            foreach (char c in s)
            {
                if (NameSpecials.IndexOf(c) >= 0)
                    sb.Append('\\');
                sb.Append(c);
            }
        }

        private static void AppendTypeName(StringBuilder sb, TypeSig sig)
        {
            sig = sig.RemovePinnedAndModifiers();
            switch (sig)
            {
            case GenericInstSig gi:
                AppendTypeName(sb, gi.GenericType);
                sb.Append('<');
                AppendNameList(sb, gi.GenericArguments);
                sb.Append('>');
                return;
            case SZArraySig sz:
                AppendTypeName(sb, sz.Next);
                sb.Append("[]");
                return;
            case ArraySig arr:
                AppendTypeName(sb, arr.Next);
                sb.Append('[').Append(new string(',', (int)Math.Max(arr.Rank, 1) - 1)).Append(']');
                return;
            case PtrSig p:
                AppendTypeName(sb, p.Next);
                sb.Append('*');
                return;
            case ByRefSig r:
                AppendTypeName(sb, r.Next);
                sb.Append('&');
                return;
            case TypeDefOrRefSig tdr:
            {
                TypeDef td = ResolveTypeDef(tdr.TypeDefOrRef) ?? throw new InvalidOperationException($"cannot resolve {sig.FullName}");
                sb.Append('[');
                AppendEscaped(sb, td.Module.Assembly.Name);
                sb.Append(']');
                AppendEscaped(sb, td.FullName);
                return;
            }
            default:
                throw new NotSupportedException($"type argument {sig.FullName} has no name key");
            }
        }

        /// <summary>Parses a <see cref="NameKey"/> against the assemblies in <paramref name="cache"/>; null with a reason when
        /// the instantiation no longer exists there (renamed type, removed method, missing assembly).</summary>
        public static GenericInstantiation ParseNameKey(string key, AssemblyCache cache, Func<ModuleDef, IReadOnlyDictionary<string, MethodDef>> methodsByFullName, out string error)
        {
            error = null;
            string[] parts = SplitTopLevel(key, '|');
            if (parts.Length != 4)
            {
                error = "expected 4 fields";
                return null;
            }
            ModuleDef mod = TryLoad(cache, parts[0]);
            if (mod == null)
            {
                error = $"assembly {parts[0]} not found";
                return null;
            }
            if (!methodsByFullName(mod).TryGetValue(parts[1], out MethodDef def))
            {
                error = $"method {parts[1]} not found in {parts[0]}";
                return null;
            }
            try
            {
                var classArgs = ParseNameList(parts[2], cache);
                var methodArgs = ParseNameList(parts[3], cache);
                var gi = TryCreate(def, classArgs, methodArgs);
                if (gi == null)
                    error = "arguments do not fit the definition";
                return gi;
            }
            catch (FormatException e)
            {
                error = e.Message;
                return null;
            }
        }

        private static ModuleDef TryLoad(AssemblyCache cache, string name)
        {
            try
            {
                return cache.LoadModule(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string[] SplitTopLevel(string s, char sep)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    cur.Append(c).Append(s[++i]);
                    continue;
                }
                if (c == sep)
                {
                    parts.Add(cur.ToString());
                    cur.Clear();
                    continue;
                }
                cur.Append(c);
            }
            parts.Add(cur.ToString());
            return parts.ToArray();
        }

        private static List<TypeSig> ParseNameList(string s, AssemblyCache cache)
        {
            var list = new List<TypeSig>();
            if (s.Length == 0)
                return list;
            int pos = 0;
            list.Add(ParseTypeName(s, ref pos, cache));
            while (pos < s.Length && s[pos] == ',')
            {
                pos++;
                list.Add(ParseTypeName(s, ref pos, cache));
            }
            if (pos != s.Length)
                throw new FormatException($"unexpected '{s[pos]}' at {pos} in {s}");
            return list;
        }

        private static TypeSig ParseTypeName(string s, ref int pos, AssemblyCache cache)
        {
            if (pos >= s.Length || s[pos] != '[')
                throw new FormatException($"expected [assembly] at {pos} in {s}");
            pos++;
            string asm = ReadName(s, ref pos);
            if (pos >= s.Length || s[pos] != ']')
                throw new FormatException($"expected ] at {pos} in {s}");
            pos++;
            string fullName = ReadName(s, ref pos);
            ModuleDef mod = TryLoad(cache, asm) ?? throw new FormatException($"assembly {asm} not found");
            TypeDef td = mod.Find(fullName, false) ?? throw new FormatException($"type {fullName} not found in {asm}");
            TypeSig sig;
            if (TryGetPrimitive(td, out _))
                sig = (TypeSig)mod.CorLibTypes.GetCorLibTypeSig(td) ?? (td.IsValueType ? new ValueTypeSig(td) : new ClassSig(td));
            else
                sig = td.IsValueType ? new ValueTypeSig(td) : new ClassSig(td);
            if (pos < s.Length && s[pos] == '<')
            {
                pos++;
                var args = new List<TypeSig> { ParseTypeName(s, ref pos, cache) };
                while (pos < s.Length && s[pos] == ',')
                {
                    pos++;
                    args.Add(ParseTypeName(s, ref pos, cache));
                }
                if (pos >= s.Length || s[pos] != '>')
                    throw new FormatException($"expected > at {pos} in {s}");
                pos++;
                sig = new GenericInstSig((ClassOrValueTypeSig)sig, args);
            }
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '[')
                {
                    int rank = 1;
                    pos++;
                    while (pos < s.Length && s[pos] == ',')
                    {
                        rank++;
                        pos++;
                    }
                    if (pos >= s.Length || s[pos] != ']')
                        throw new FormatException($"expected ] at {pos} in {s}");
                    pos++;
                    sig = rank == 1 ? new SZArraySig(sig) : new ArraySig(sig, (uint)rank);
                }
                else if (c == '*')
                {
                    pos++;
                    sig = new PtrSig(sig);
                }
                else if (c == '&')
                {
                    pos++;
                    sig = new ByRefSig(sig);
                }
                else
                {
                    break;
                }
            }
            return sig;
        }

        private static string ReadName(string s, ref int pos)
        {
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '\\' && pos + 1 < s.Length)
                {
                    sb.Append(s[pos + 1]);
                    pos += 2;
                    continue;
                }
                if (NameSpecials.IndexOf(c) >= 0)
                    break;
                sb.Append(c);
                pos++;
            }
            return sb.ToString();
        }
    }
}
