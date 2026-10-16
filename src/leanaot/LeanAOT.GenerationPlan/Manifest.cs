using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;
using LeanAOT.Core;

namespace LeanAOT.GenerationPlan
{

    public class ManifestArgs
    {
        public AssemblyCache assemblyCache;

        public List<string> aotAssemblyNames;

        /// <summary>
        /// When non-null, method inclusion after attribute/intrinsic checks follows <c>aot.xml</c> rules (see design doc).
        /// </summary>
        public AotMethodRulesEvaluator AotRulesEvaluator;

        /// <summary>
        /// When non-null, profiled methods in PGO rule files are force-included even if <c>aot.xml</c> excluded them.
        /// </summary>
        public PgoMethodIncludeIndex PgoIncludeIndex;

        /// <summary>
        /// Optional runtime-profile gate for methods that must stay on the interpreter path.
        /// </summary>
        public Func<MethodDef, bool> ShouldSkipUnsupportedRuntimeMethod;

        /// <summary>icall / P/Invoke methods to leave to the runtime's own invokers (no direct runtime API entry).</summary>
        public Func<MethodDef, bool> ShouldSkipRuntimeResolvedNativeMethod;

        /// <summary>When non-null, closed generic instantiations are compiled too (<see cref="GenericPlanner"/>).</summary>
        public GenericPlannerArgs Generics;
    }

    public class Manifest
    {

        private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly Dictionary<string, AssemblyPlan> _assemblyPlans = new Dictionary<string, AssemblyPlan>();

        public IReadOnlyDictionary<string, AssemblyPlan> AssemblyPlans => _assemblyPlans;

        private readonly Dictionary<string, HashSet<string>> _closures = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>Per AOT module: the module and every assembly in its reference closure (what its MVID binding covers).</summary>
        public IReadOnlyDictionary<string, HashSet<string>> Closures => _closures;

        public GenericPlanner Generics { get; }

        public IEnumerable<GenericMethodPlan> GenericMethodPlans => Generics?.Plans.Values ?? Enumerable.Empty<GenericMethodPlan>();

        /// <summary>The instantiations whose code goes into <paramref name="home"/>'s files, in a stable order.</summary>
        public List<GenericMethodPlan> GetGenericPlansForHome(string home)
        {
            return GenericMethodPlans.Where(p => p.Home == home).OrderBy(p => p.Inst.RidKey, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Per assembly: why methods with IL bodies were left to the interpreter (reason -> method full names).
        /// Reasons: "generic method", "generic type", "exception handlers", "vararg", "rules", "unsupported intrinsic",
        /// "icall / P/Invoke resolved at run time" (no IL body; listed so reports can count them).
        /// </summary>
        public Dictionary<string, SortedDictionary<string, List<string>>> SkippedMethods { get; } = new Dictionary<string, SortedDictionary<string, List<string>>>();

        // non-generic methods that stay interpreted only because of exception handlers (scanned for generic callees)
        private readonly List<MethodDef> _interpretedRoots = new List<MethodDef>();

        private void NoteSkipped(string assName, string reason, MethodDef method, bool native = false)
        {
            if (!method.HasBody && !native)
                return;
            if (!SkippedMethods.TryGetValue(assName, out var byReason))
                SkippedMethods[assName] = byReason = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            if (!byReason.TryGetValue(reason, out var list))
                byReason[reason] = list = new List<string>();
            list.Add(method.FullName);
        }

        private void TryAddMonoPInvokeCallbackPlan(List<MethodDefPlan> monoPInvokeCallbackPlans, MethodDef method)
        {
            var ca = method.CustomAttributes.FirstOrDefault(MetaUtil.IsMonoPInvokeCallbackAttribute);
            if (ca == null)
            {
                return;
            }
            if (ca.ConstructorArguments.Count != 1 || ca.ConstructorArguments[0].Type.FullName != "System.Type")
            {
                s_logger.Warn($"Skip method with invalid MonoPInvokeCallbackAttribute: {method.FullName}. Expected exactly one argument of type System.Type.");
                return;
            }
            if (!method.IsStatic)
            {
                s_logger.Warn($"Skip method with non-static MonoPInvokeCallbackAttribute: {method.FullName} token: {method.MDToken}");
                return;
            }
            if (!method.HasBody)
            {
                s_logger.Warn($"Skip method with no body: {method.FullName} token: {method.MDToken}");
                return;
            }
            monoPInvokeCallbackPlans.Add(new MethodDefPlan { MethodDef = method });
        }

        public Manifest(ManifestArgs args)
        {
            foreach (var assName in args.aotAssemblyNames)
            {

                var mod = args.assemblyCache.LoadModule(assName);
                var classPlans = new List<ClassPlan>();
                var methodPlans = new List<MethodDefPlan>();
                var monoPInvokeCallbackPlans = new List<MethodDefPlan>();
                var methodsInAotPlan = new HashSet<IMethod>(MethodEqualityComparer.CompareDeclaringTypes);
                foreach (TypeDef type in mod.GetTypes())
                {
                    var classPlan = new ClassPlan()
                    {
                        TypeDef = type,
                    };
                    classPlans.Add(classPlan);
                    foreach (var method in type.Methods)
                    {
                        if (method.IsRuntime || method.IsAbstract)
                            continue;
                        if (method.HasGenericParameters || method.DeclaringType.HasGenericParameters)
                        {
                            NoteSkipped(assName, method.HasGenericParameters ? "generic method" : "generic type", method);
                            continue;
                        }
                        if (method.HasBody && method.Body.HasExceptionHandlers)
                        {
                            NoteSkipped(assName, "exception handlers", method);
                            // still runs (interpreted) and calls generic code: its callees belong to the static closure
                            if (args.AotRulesEvaluator == null || args.AotRulesEvaluator.ShouldIncludeByRules(assName, method) ||
                                (args.PgoIncludeIndex != null && args.PgoIncludeIndex.Contains(assName, method)))
                            {
                                _interpretedRoots.Add(method);
                            }
                            continue;
                        }
                        // don't aot System.String's constructor, because we will handle it in a special way
                        if (!method.HasBody && method.IsConstructor && type.FullName == "System.String")
                        {
                            continue;
                        }
                        if (method.CallingConvention == CallingConvention.VarArg)
                        {
                            NoteSkipped(assName, "vararg", method);
                            s_logger.Warn($"Skip method with vararg calling convention: {method.FullName} token: {method.MDToken}");
                            continue;
                        }
                        TryAddMonoPInvokeCallbackPlan(monoPInvokeCallbackPlans, method);
                        string typeName = method.DeclaringType.Name;
                        CustomAttribute ca = method.CustomAttributes.FirstOrDefault(ca => ca.TypeFullName == "AotMethodAttribute");
                        if (ca != null)
                        {
                            bool isAotMethod = (bool)ca.ConstructorArguments[0].Value;
                            if (!isAotMethod)
                            {
                                continue;
                            }
                            TryAddMethodPlan(methodPlans, methodsInAotPlan, method);
                            continue;
                        }
                        if (method.IsPinvokeImpl || method.IsInternalCall)
                        {
                            if (args.ShouldSkipRuntimeResolvedNativeMethod?.Invoke(method) == true)
                            {
                                NoteSkipped(assName, "icall / P/Invoke resolved at run time", method, native: true);
                                continue;
                            }
                            // aot or intrinsic methods must be aot
                            TryAddMethodPlan(methodPlans, methodsInAotPlan, method);
                            continue;
                        }

                        if (args.ShouldSkipUnsupportedRuntimeMethod?.Invoke(method) == true)
                        {
                            NoteSkipped(assName, "unsupported intrinsic", method);
                            s_logger.Debug($"[Manifest] Skip method (unsupported runtime intrinsic): {method.FullName} token: {method.MDToken}");
                            continue;
                        }

                        // 8.3–8.4: rule files; no match => AOT (design). PGO only adds hot paths on top.
                        if (args.AotRulesEvaluator != null && !args.AotRulesEvaluator.ShouldIncludeByRules(assName, method))
                        {
                            if (args.PgoIncludeIndex == null || !args.PgoIncludeIndex.Contains(assName, method))
                            {
                                NoteSkipped(assName, "rules", method);
                                s_logger.Debug($"[Manifest] Skip method (AOT rules): {method.FullName} token: {method.MDToken}");
                                continue;
                            }
                            s_logger.Debug($"[Manifest] PGO include overrides AOT rule exclusion: {method.FullName} token: {method.MDToken}");
                        }

                        TryAddMethodPlan(methodPlans, methodsInAotPlan, method);
                    }
                }
                var assPlan = new AssemblyPlan(mod, assName, classPlans, methodPlans, monoPInvokeCallbackPlans);
                if (SkippedMethods.TryGetValue(assName, out var skipped))
                {
                    s_logger.Info($"[Manifest] {assName}: {methodPlans.Count} methods AOT, interpreted: {string.Join(", ", skipped.Select(kv => $"{kv.Value.Count} {kv.Key}"))}");
                }

                _assemblyPlans[assName] = assPlan;
            }

            foreach (var assName in _assemblyPlans.Keys)
            {
                _closures[assName] = ComputeClosure(args.assemblyCache, assName);
            }
            if (args.Generics != null)
            {
                Generics = new GenericPlanner(args.assemblyCache, args.Generics, _closures);
                Generics.Run(_assemblyPlans.Values.SelectMany(p => p.MethodPlans).Select(p => p.MethodDef).Concat(_interpretedRoots));
            }
        }

        private static HashSet<string> ComputeClosure(AssemblyCache cache, string assName)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(assName);
            while (stack.Count > 0)
            {
                string name = stack.Pop();
                if (!seen.Add(name))
                    continue;
                ModuleDefMD mod = cache.LoadModule(name);
                foreach (AssemblyRef ar in mod.GetAssemblyRefs())
                    stack.Push(ar.Name);
            }
            return seen;
        }

        private static void TryAddMethodPlan(List<MethodDefPlan> methodPlans, HashSet<IMethod> methodsInAotPlan, MethodDef method)
        {
            if (!methodsInAotPlan.Add(method))
            {
                return;
            }
            var methodPlan = new MethodDefPlan()
            {
                MethodDef = method,
            };
            s_logger.Debug($"[Manifest] Add MethodDefPlan: {method.FullName}");
            methodPlans.Add(methodPlan);
        }

        public bool ShouldAOT(IMethod method)
        {
            return ShouldAOT(method, null);
        }

        /// <summary>Is <paramref name="method"/> compiled, and may code generated into <paramref name="callerHome"/> call
        /// it directly? An instantiation only when its home's binding closure is part of the caller's: then whenever
        /// the caller's MVID binding holds, so does the callee's, and a direct call never reaches code generated for
        /// other images. Otherwise the call goes through the runtime, which only uses the instantiation's code when its
        /// own home module is attached.</summary>
        public bool ShouldAOT(IMethod method, string callerHome)
        {
            MethodDef methodDef = method.ResolveMethodDef();
            if (methodDef == null)
            {
                return false;
            }
            if (GenericInstantiation.IsGenericDefinition(methodDef))
            {
                GenericMethodPlan plan = TryGetGenericPlan(method);
                if (plan == null || callerHome == null)
                {
                    return false;
                }
                return plan.Home == callerHome || (_closures.TryGetValue(callerHome, out var callerClosure) && _closures[plan.Home].IsSubsetOf(callerClosure));
            }
            return AssemblyPlans.TryGetValue(methodDef.Module.Assembly.Name, out var assPlan) &&
                assPlan.ContaisAOTMethod(method);
        }

        public GenericMethodPlan TryGetGenericPlan(IMethod method)
        {
            if (Generics == null)
            {
                return null;
            }
            GenericInstantiation gi = GenericInstantiation.TryFrom(method);
            return gi != null && Generics.Plans.TryGetValue(gi.RidKey, out var plan) ? plan : null;
        }
    }

}
