using dnlib.DotNet;
using dnlib.DotNet.Emit;
using LeanAOT.Core;

namespace LeanAOT.GenerationPlan
{
    public class GenericPlannerArgs
    {
        /// <summary>Name-keyed instantiation lists (one <see cref="GenericInstantiation.NameKey"/> per line, '#' comments),
        /// e.g. runtime/aot/generics-hot.txt written by tests/runtime/aot-coverage.mjs from a profiled replay.</summary>
        public IReadOnlyList<string> ListFiles = Array.Empty<string>();

        /// <summary>Methods the runtime implements itself (generic intrinsics / icalls); their IL bodies are not the
        /// implementation, so they are never compiled and calls to them go through the runtime.</summary>
        public Func<MethodDef, bool> IsRuntimeImplemented;

        public Func<MethodDef, bool> ShouldSkipUnsupportedRuntimeMethod;

        /// <summary>Instantiations whose method full name contains one of these strings stay interpreted (diagnostics:
        /// bisecting a miscompiled instantiation; a workaround until the generator is fixed).</summary>
        public IReadOnlyList<string> Exclude = Array.Empty<string>();

        /// <summary>Deepest nesting of generic types in the arguments that is still compiled.</summary>
        public int MaxDepth = 6;

        /// <summary>Upper bound on compiled instantiations (a guard; the measured closure is far smaller).</summary>
        public int MaxPlans = 30000;
    }

    /// <summary>
    /// Chooses the closed generic instantiations to compile:
    ///   static    every instantiation that compiled code (non-generic methods, then the instantiations themselves,
    ///             transitively) calls, constructs, loads with ldftn / ldtoken. Needs nothing but the DLLs: the floor.
    ///   virtual   overrides of base-class virtuals on a generic type that compiled code constructs (newobj): the
    ///             runtime reaches them through the vtable, which no call site names.
    ///   comparer  EqualityComparer&lt;T&gt;.Default / Comparer&lt;T&gt;.Default build their comparer by reflection; the
    ///             comparer type the runtime will pick (GenericComparer&lt;T&gt;, ObjectEqualityComparer&lt;T&gt;, ...) is
    ///             predicted like System.Collections.Generic.ComparerHelpers does.
    ///   profile   instantiations that ran in a profiled replay, by name (runtime/aot/generics-hot.txt): interface
    ///             dispatch, reflection, anything the static walk cannot see. Entries the current DLLs no longer have
    ///             are skipped.
    /// and the module each one lives in (<see cref="GenericMethodPlan.Home"/>).
    /// </summary>
    public class GenericPlanner
    {
        private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly AssemblyCache _cache;
        private readonly GenericPlannerArgs _args;
        private readonly IReadOnlyDictionary<string, HashSet<string>> _closures;
        private readonly Dictionary<string, GenericMethodPlan> _plans = new Dictionary<string, GenericMethodPlan>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<GenericMethodPlan> _queue = new Queue<GenericMethodPlan>();
        private readonly Queue<GenericInstantiation> _interpretedScans = new Queue<GenericInstantiation>();
        private readonly HashSet<string> _virtualsAdded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<ModuleDef, Dictionary<string, MethodDef>> _methodsByFullName = new Dictionary<ModuleDef, Dictionary<string, MethodDef>>();

        public IReadOnlyDictionary<string, GenericMethodPlan> Plans => _plans;

        /// <summary>reason -> instantiations (full names) that stay interpreted.</summary>
        public SortedDictionary<string, List<string>> Skipped { get; } = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        public int ListEntries { get; private set; }

        public int ListEntriesMissing { get; private set; }

        public GenericPlanner(AssemblyCache cache, GenericPlannerArgs args, IReadOnlyDictionary<string, HashSet<string>> closures)
        {
            _cache = cache;
            _args = args;
            _closures = closures;
        }

        public void Run(IEnumerable<MethodDef> compiledMethods)
        {
            foreach (MethodDef m in compiledMethods)
            {
                if (m.HasBody)
                    Scan(m.Body, null);
            }
            Drain();
            foreach (string file in _args.ListFiles)
            {
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    ListEntries++;
                    var gi = GenericInstantiation.ParseNameKey(line, _cache, MethodsByFullName, out string error);
                    if (gi == null)
                    {
                        ListEntriesMissing++;
                        s_logger.Debug($"[Generics] list entry skipped ({error}): {line}");
                        continue;
                    }
                    Consider(gi, "profile");
                }
            }
            Drain();
            if (_args.Exclude.Count > 0)
            {
                foreach (var plan in _plans.Values.Where(p => _args.Exclude.Any(x => p.Inst.Method.FullName.Contains(x, StringComparison.Ordinal))).ToList())
                {
                    Drop(plan.Inst.RidKey, "excluded");
                }
            }
            s_logger.Info($"[Generics] {_plans.Count} instantiations compiled ({string.Join(", ", _plans.Values.GroupBy(p => p.Origin).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"))}); " +
                $"list entries {ListEntries} ({ListEntriesMissing} not in these DLLs); interpreted: {string.Join(", ", Skipped.Select(kv => $"{kv.Value.Count} {kv.Key}"))}");
        }

        /// <summary>Takes a plan out again (its code could not be generated); callers then go through the runtime.</summary>
        public void Drop(string ridKey, string reason)
        {
            if (_plans.Remove(ridKey, out var plan))
            {
                _rejected[ridKey] = reason;
                NoteSkipped(reason, plan.Inst);
            }
        }

        private IReadOnlyDictionary<string, MethodDef> MethodsByFullName(ModuleDef mod)
        {
            if (!_methodsByFullName.TryGetValue(mod, out var map))
            {
                map = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
                foreach (TypeDef t in mod.GetTypes())
                {
                    foreach (MethodDef m in t.Methods)
                    {
                        if (GenericInstantiation.IsGenericDefinition(m))
                            map.TryAdd(m.FullName, m);
                    }
                }
                _methodsByFullName[mod] = map;
            }
            return map;
        }

        private void Drain()
        {
            while (_queue.Count > 0 || _interpretedScans.Count > 0)
            {
                if (_queue.Count > 0)
                {
                    GenericMethodPlan plan = _queue.Dequeue();
                    Scan(plan.Inst.Def.Body, plan.Inst.GAC);
                }
                else
                {
                    GenericInstantiation gi = _interpretedScans.Dequeue();
                    Scan(gi.Def.Body, gi.GAC);
                }
            }
        }

        private void NoteSkipped(string reason, GenericInstantiation gi)
        {
            if (!Skipped.TryGetValue(reason, out var list))
                Skipped[reason] = list = new List<string>();
            list.Add(gi.Method.FullName);
        }

        private void Scan(CilBody body, GenericArgumentContext gac)
        {
            TypeSig constrained = null;
            foreach (Instruction inst in body.Instructions)
            {
                if (inst.OpCode.Code == Code.Constrained)
                {
                    try
                    {
                        constrained = inst.Operand is ITypeDefOrRef ct ? (gac == null ? ct.ToTypeSig() : MetaUtil.Inflate(ct.ToTypeSig(), gac)) : null;
                    }
                    catch (Exception)
                    {
                        constrained = null;
                    }
                    continue;
                }
                TypeSig constrainedType = constrained;
                constrained = null;
                if (inst.Operand is not IMethod m || !m.IsMethod)
                    continue;
                IMethod inflated;
                try
                {
                    inflated = gac == null ? m : MetaUtil.InflateMethod(m, gac);
                }
                catch (Exception)
                {
                    continue;
                }
                GenericInstantiation gi = GenericInstantiation.TryFrom(inflated);
                if (gi != null)
                {
                    Consider(gi, "static");
                    if (IsDefaultComparerGetter(gi.Def, out bool equality))
                        AddDefaultComparer(gi.ClassArgs[0], equality);
                }
                if (inst.OpCode.Code == Code.Newobj)
                    AddOverrides(inflated.DeclaringType.ToTypeSig(), "virtual");
                if (constrainedType != null)
                    AddConstrainedTarget(constrainedType, inflated);
            }
        }

        /// <summary>
        /// <c>constrained. T call / callvirt M</c>: the method that runs is T's implementation of M (foreach over a struct
        /// enumerator disposes it through IDisposable; MemoryPack calls T.Serialize&lt;TBufferWriter&gt; through
        /// IMemoryPackable&lt;T&gt;). Over-approximates (explicit implementations, or virtual methods with the same name and
        /// arity); an extra plan only costs code size.
        /// </summary>
        private void AddConstrainedTarget(TypeSig constrainedType, IMethod called)
        {
            TypeSig t = constrainedType.RemovePinnedAndModifiers();
            if (t == null || t.ContainsGenericParameter)
                return;
            IList<TypeSig> classArgs = Array.Empty<TypeSig>();
            TypeDef td;
            if (t is GenericInstSig gis)
            {
                td = GenericInstantiation.ResolveTypeDef(gis.GenericType.TypeDefOrRef);
                classArgs = gis.GenericArguments;
            }
            else if (t is TypeDefOrRefSig tdr)
            {
                td = GenericInstantiation.ResolveTypeDef(tdr.TypeDefOrRef);
            }
            else
            {
                return;
            }
            MethodDef calledDef = called.ResolveMethodDef();
            if (td == null || calledDef == null || td.GenericParameters.Count != classArgs.Count)
                return;
            IList<TypeSig> methodArgs = called is MethodSpec ms ? ms.GenericInstMethodSig.GenericArguments : Array.Empty<TypeSig>();
            if (classArgs.Count == 0 && methodArgs.Count == 0)
                return; // a plain method, compiled (or not) with its assembly
            foreach (MethodDef m in td.Methods)
            {
                if (m.GenericParameters.Count != methodArgs.Count || m.IsStatic != calledDef.IsStatic || !m.HasBody)
                    continue;
                bool explicitImpl = m.Overrides.Any(o => o.MethodDeclaration.ResolveMethodDef() == calledDef);
                bool sameName = !m.IsStatic && m.IsVirtual && m.Name == calledDef.Name && m.MethodSig.Params.Count == calledDef.MethodSig.Params.Count;
                if (!explicitImpl && !sameName)
                    continue;
                var gi = GenericInstantiation.TryCreate(m, classArgs, methodArgs);
                if (gi != null)
                    Consider(gi, "static");
            }
        }

        private void Consider(GenericInstantiation gi, string origin)
        {
            if (_plans.ContainsKey(gi.RidKey) || _rejected.ContainsKey(gi.RidKey))
                return;
            string home = null;
            string reason = Check(gi) ?? ((home = FindHome(gi)) == null ? "no module covers its assemblies" : null);
            if (reason != null)
            {
                _rejected[gi.RidKey] = reason;
                NoteSkipped(reason, gi);
                if (reason == "exception handlers" || reason == "exception filter")
                {
                    // interpreted, but what it calls can still be compiled
                    _interpretedScans.Enqueue(gi);
                }
                return;
            }
            var plan = new GenericMethodPlan { Inst = gi, Home = home, Origin = origin };
            _plans.Add(gi.RidKey, plan);
            _queue.Enqueue(plan);
        }

        private string Check(GenericInstantiation gi)
        {
            MethodDef def = gi.Def;
            if (def.IsAbstract || def.IsRuntime || !def.HasBody)
                return "no IL body";
            if (def.IsStaticConstructor)
                return "static constructor";
            if (def.CallingConvention == CallingConvention.VarArg)
                return "vararg";
            string ehSkipReason = MetaUtil.GetExceptionHandlerSkipReason(def);
            if (ehSkipReason != null)
                return ehSkipReason;
            if (_args.IsRuntimeImplemented?.Invoke(def) == true)
                return "runtime intrinsic";
            if (_args.ShouldSkipUnsupportedRuntimeMethod?.Invoke(def) == true)
                return "unsupported intrinsic";
            CustomAttribute ca = def.CustomAttributes.FirstOrDefault(a => a.TypeFullName == "AotMethodAttribute");
            if (ca != null && ca.ConstructorArguments.Count == 1 && ca.ConstructorArguments[0].Value is bool b && !b)
                return "rules";
            if (gi.Depth() > _args.MaxDepth)
                return "nested too deep";
            if (_plans.Count >= _args.MaxPlans)
                return "limit";
            return null;
        }

        private string FindHome(GenericInstantiation gi)
        {
            var involved = gi.InvolvedAssemblies().ToList();
            string best = null;
            int bestSize = int.MaxValue;
            foreach (var (name, closure) in _closures.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (closure.Count < bestSize && involved.All(closure.Contains))
                {
                    best = name;
                    bestSize = closure.Count;
                }
            }
            return best;
        }

        /// <summary>Overrides of base-class virtual methods (not interface implementations: profiles show which of those
        /// run) declared on a closed generic type.</summary>
        private void AddOverrides(TypeSig type, string origin)
        {
            if (type?.RemovePinnedAndModifiers() is not GenericInstSig gis || gis.ContainsGenericParameter)
                return;
            TypeDef td = GenericInstantiation.ResolveTypeDef(gis.GenericType.TypeDefOrRef);
            if (td == null || td.GenericParameters.Count != gis.GenericArguments.Count)
                return;
            string typeKey = GenericInstantiation.TypeKey(gis);
            if (typeKey == null || !_virtualsAdded.Add(typeKey))
                return;
            foreach (MethodDef m in td.Methods)
            {
                if (!m.IsVirtual || m.IsNewSlot || m.IsAbstract || m.HasGenericParameters || !m.HasBody)
                    continue;
                var gi = GenericInstantiation.TryCreate(m, gis.GenericArguments, Array.Empty<TypeSig>());
                if (gi != null)
                    Consider(gi, origin);
            }
        }

        private static bool IsDefaultComparerGetter(MethodDef def, out bool equality)
        {
            equality = false;
            if (def.Name != "get_Default" || def.Module.Assembly.Name != GenericInstantiation.CoreLibName)
                return false;
            string t = def.DeclaringType.FullName;
            equality = t == "System.Collections.Generic.EqualityComparer`1";
            return equality || t == "System.Collections.Generic.Comparer`1";
        }

        /// <summary>The comparer EqualityComparer&lt;T&gt;.Default / Comparer&lt;T&gt;.Default creates (ComparerHelpers in
        /// System.Private.CoreLib): IEquatable / IComparable implementers get the generic comparer, Nullable and enums
        /// their own, everything else the object comparer.</summary>
        private void AddDefaultComparer(TypeSig t, bool equality)
        {
            ModuleDef corlib;
            try
            {
                corlib = _cache.LoadModule(GenericInstantiation.CoreLibName);
            }
            catch (Exception)
            {
                return;
            }
            string iface = equality ? "System.IEquatable`1" : "System.IComparable`1";
            string comparer;
            TypeSig arg = t;
            if (t.ElementType == ElementType.String && equality)
            {
                comparer = "GenericEqualityComparer`1";
            }
            else if (Implements(t, iface))
            {
                comparer = equality ? "GenericEqualityComparer`1" : "GenericComparer`1";
            }
            else if (t is GenericInstSig gi && gi.GenericType.FullName == "System.Nullable`1" && Implements(gi.GenericArguments[0], iface))
            {
                comparer = equality ? "NullableEqualityComparer`1" : "NullableComparer`1";
                arg = gi.GenericArguments[0];
            }
            else if (GenericInstantiation.ResolveTypeDef(t.ToTypeDefOrRef())?.IsEnum == true)
            {
                comparer = equality ? "EnumEqualityComparer`1" : "EnumComparer`1";
            }
            else
            {
                comparer = equality ? "ObjectEqualityComparer`1" : "ObjectComparer`1";
            }
            TypeDef td = corlib.Find("System.Collections.Generic." + comparer, false);
            if (td == null)
                return;
            AddOverrides(new GenericInstSig(new ClassSig(td), arg), "comparer");
        }

        /// <summary>Does <paramref name="t"/> (or a base type) implement <paramref name="ifaceFullName"/>&lt;t&gt;?</summary>
        private static bool Implements(TypeSig t, string ifaceFullName)
        {
            t = t.RemovePinnedAndModifiers();
            string self = KeyOf(t);
            if (self == null)
                return false;
            TypeSig cur = t;
            for (int guard = 0; cur != null && guard < 32; guard++)
            {
                TypeDef td = GenericInstantiation.ResolveTypeDef(cur.ToTypeDefOrRef());
                if (td == null)
                    return false;
                var gac = cur is GenericInstSig gis ? new GenericArgumentContext(gis.GenericArguments, null) : null;
                foreach (InterfaceImpl ii in td.Interfaces)
                {
                    TypeSig isig = MetaUtil.Inflate(ii.Interface.ToTypeSig(), gac);
                    if (isig is GenericInstSig ig && ig.GenericType.FullName == ifaceFullName && ig.GenericArguments.Count == 1 && KeyOf(ig.GenericArguments[0]) == self)
                        return true;
                }
                cur = td.BaseType == null ? null : MetaUtil.Inflate(td.BaseType.ToTypeSig(), gac);
            }
            return false;
        }

        private static string KeyOf(TypeSig t) => GenericInstantiation.TypeKey(t);
    }
}
