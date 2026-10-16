using dnlib.DotNet;
using LeanAOT.Core;
using LeanAOT.GenerationPlan;

namespace LeanAOT.ToCpp
{
    class MethodGenerationException : Exception
    {
        public MethodDef MethodDef { get; }
        public MethodGenerationException(MethodDef methodDef, string message, Exception innerException) : base(message, innerException)
        {
            MethodDef = methodDef;
        }
    }

    class AssemblyGenerationContext
    {
        public GenerationConfig config;

        public AssemblyPlan plan;

        private int fileIndex = 0;

        private readonly List<ModuleMethodBodyCodeFilePart> _methodBodyCodeFiles = new List<ModuleMethodBodyCodeFilePart>();

        public ModuleMethodBodyCodeFilePart CreateMethodBodyCodeFileForNextFile()
        {
            fileIndex++;
            var methodBodyCodeFile = new ModuleMethodBodyCodeFilePart($"{config.outputCodeDir}/{ModuleGenerationUtil.GetModuleMethodBodyFileNameWithExt(plan.Module, fileIndex)}", plan.Module);
            _methodBodyCodeFiles.Add(methodBodyCodeFile);
            return methodBodyCodeFile;
        }

        public void SaveAll()
        {
            foreach (var methodBodyCodeFile in _methodBodyCodeFiles)
            {
                methodBodyCodeFile.Save();
            }
        }
    }

    class MethodDefGenerationContext
    {
        public ModuleMethodBodyCodeFilePart methodBodyCodeFile;

        public AssemblyGenerationContext assemblyContext;
    }

    public class CppGenerator : GeneratorBase
    {
        private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly Dictionary<string, AssemblyGenerationContext> _assemblyContexts = new Dictionary<string, AssemblyGenerationContext>();

        private AssemblyGenerationContext GetAssemblyContext(AssemblyPlan plan)
        {
            if (!_assemblyContexts.TryGetValue(plan.AssemblyName, out var context))
            {
                context = new AssemblyGenerationContext
                {
                    config = _config,
                    plan = plan,
                };
                _assemblyContexts[plan.AssemblyName] = context;
            }
            return context;
        }

        /// <summary>
        /// Generates every planned generic instantiation once into a throw-away file part first: an instantiation whose
        /// body the code generator cannot handle (an intrinsic it does not know with these arguments, metadata it cannot
        /// name) is dropped from the plan before any caller decides to call it directly, so it simply stays interpreted.
        /// Uses its own invoker / bridge services so the dry run leaves nothing behind in the output.
        /// </summary>
        protected override void Prepare()
        {
            var manifest = _config.manifest;
            if (manifest.Generics == null)
            {
                return;
            }
            GlobalServices saved = GlobalServices.Inst;
            GlobalServices.Inst = new GlobalServices
            {
                Config = saved.Config,
                ManifestService = saved.ManifestService,
                TypeNameService = saved.TypeNameService,
                InvokerService = new InvokerService(saved.MetadataService),
                DirectCallBridgeService = new DirectCallBridgeService(saved.MetadataService),
                MonoPInvokeCallbackService = new MonoPInvokeCallbackService(saved.MetadataService),
                MetadataService = saved.MetadataService,
                RuntimeApiCatalog = saved.RuntimeApiCatalog,
            };
            int dropped = 0;
            var whys = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var gp in manifest.GenericMethodPlans.OrderBy(p => p.Inst.RidKey, StringComparer.Ordinal).ToList())
                {
                    GlobalServices.Inst.ManifestService.CurrentHome = gp.Home;
                    var scratch = new ModuleMethodBodyCodeFilePart(Path.Combine(_config.outputCodeDir, "dry-run.cpp"), manifest.AssemblyPlans[gp.Home].Module);
                    try
                    {
                        GenerateGenericMethod(gp, scratch);
                    }
                    catch (Exception e)
                    {
                        dropped++;
                        string why = (e is NotSupportedException || e is NotImplementedException) ? e.Message : $"{e.GetType().Name}: {e.Message}";
                        s_logger.Debug($"[Generics] dropped {gp.Inst}: {why}\n{e.StackTrace}");
                        string shortWhy = $"{e.GetType().Name} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
                        whys[shortWhy] = whys.GetValueOrDefault(shortWhy) + 1;
                        examples.TryAdd(shortWhy, $"{gp.Inst}: {why}\n{string.Join("\n", (e.StackTrace ?? "").Split('\n').Take(8))}");
                        manifest.Generics.Drop(gp.Inst.RidKey, "not supported by the code generator");
                    }
                }
            }
            finally
            {
                GlobalServices.Inst = saved;
                saved.ManifestService.CurrentHome = null;
            }
            s_logger.Info($"[Generics] dry run: {dropped} instantiations dropped, {manifest.Generics.Plans.Count} to generate");
            foreach (var (why, n) in whys.OrderByDescending(kv => kv.Value).Take(12))
            {
                s_logger.Info($"[Generics]   {n} x {why}");
                s_logger.Debug($"[Generics]     e.g. {examples[why]}");
            }
        }

        private void GenerateGenericMethod(GenericMethodPlan gp, IMethodBodyCodeFilePart file)
        {
            var methodDetail = GlobalServices.Inst.MetadataService.GetMethodDetail(gp.Method);
            GlobalServices.Inst.InvokerService.GetInvoker(gp.Method);
            new MethodWriter(methodDetail, file).WriteCode();
            if (methodDetail.ShouldGenerateVirtualMethod)
            {
                new VirtualMethodWriter(methodDetail, file).WriteCode();
            }
        }

        public override void GenerateAssembly(AssemblyPlan plan)
        {
            GlobalServices.Inst.ManifestService.CurrentHome = plan.AssemblyName;
            GenerateModuleRegistrationHeader(plan);
            GenerateModuleRegistrationCpp(plan);
            GenerateModuleMethodBodyRegistrationCpp(plan);
        }

        private void GenerateModuleRegistrationHeader(AssemblyPlan plan)
        {
            var context = GetAssemblyContext(plan);
            var registrationHeaderCodeFile = new ModuleRegistrationHeaderCodeFile($"{context.config.outputCodeDir}/{ModuleGenerationUtil.GetModuleRegistrationHeaderFileNameWithExt(plan.Module)}", plan.Module);
            registrationHeaderCodeFile.Generate();
            registrationHeaderCodeFile.Save();
        }

        private void GenerateModuleRegistrationCpp(AssemblyPlan plan)
        {
            var context = GetAssemblyContext(plan);
            var moduleRegistrationCppCodeFile = new ModuleRegistrationCppCodeFile($"{context.config.outputCodeDir}/{ModuleGenerationUtil.GetModuleRegistrationCppFileNameWithExt(plan.Module)}", plan, context.config.assemblyCache);
            moduleRegistrationCppCodeFile.Generate();
            moduleRegistrationCppCodeFile.Save();
        }

        private void GenerateModuleMethodBodyRegistrationCpp(AssemblyPlan plan)
        {
            var context = GetAssemblyContext(plan);
            var methodBodyCodeFile = context.CreateMethodBodyCodeFileForNextFile();
            foreach (var methodPlan in plan.MethodPlans)
            {
                if (methodBodyCodeFile.ExceedsMaxSize())
                {
                    methodBodyCodeFile = context.CreateMethodBodyCodeFileForNextFile();
                }
                var methodCtx = new MethodDefGenerationContext
                {
                    methodBodyCodeFile = methodBodyCodeFile,
                    assemblyContext = context,
                };
                GenerateMethodDef(methodPlan, methodCtx);
            }
            foreach (var gp in _config.manifest.GetGenericPlansForHome(plan.AssemblyName))
            {
                if (methodBodyCodeFile.ExceedsMaxSize())
                {
                    methodBodyCodeFile = context.CreateMethodBodyCodeFileForNextFile();
                }
                try
                {
                    GenerateGenericMethod(gp, methodBodyCodeFile);
                }
                catch (Exception e)
                {
                    throw new Exception($"Error generating instantiation {gp.Inst} (passed the dry run)", e);
                }
            }
            context.SaveAll();
        }
        public override void GenerateMethodDef(MethodDefPlan plan, object ctx)
        {
            try
            {
                GenerateMethodDefSelf(plan.MethodDef, ctx);
                GenerateVirtualMethodDefSelf(plan.MethodDef, ctx);
            }
            catch (Exception e)
            {
                throw new MethodGenerationException(plan.MethodDef, $"Error generating method {plan.MethodDef.FullName}", e);
            }
        }

        private void GenerateMethodDefSelf(MethodDef methodDef, object ctx)
        {
            var invokerService = GlobalServices.Inst.InvokerService;
            invokerService.GetInvoker(methodDef);
            var methodCtx = (MethodDefGenerationContext)ctx;
            var methodDetail = GlobalServices.Inst.MetadataService.GetMethodDetail(methodDef);
            var runtimeApiCatalog = GlobalServices.Inst.RuntimeApiCatalog;
            if (runtimeApiCatalog.TryGetIcallOrIntrinsic(methodDef, out var entry, out var _methodKind))
            {
                var icallOrIntrinsicMethodWriter = new ICallOrIntrinsicMethodWriter(methodDetail, methodCtx.methodBodyCodeFile, entry);
                icallOrIntrinsicMethodWriter.WriteCode();
                return;
            }
            if (!methodDef.HasBody)
            {
                if (methodDef.IsPinvokeImpl)
                {
                    var pinvokeMethodWriter = new PInvokeMethodWriter(methodDetail, methodCtx.methodBodyCodeFile);
                    pinvokeMethodWriter.WriteCode();
                    return;
                }
                else if (methodDef.IsInternalCall)
                {
                    var runtimeResolvedICallMethodWriter = new RuntimeResolvedICallMethodWriter(methodDetail, methodCtx.methodBodyCodeFile);
                    runtimeResolvedICallMethodWriter.WriteCode();
                    return;
                }
                // s_logger.Warn($"Method {methodDef.FullName} has no body and doesn't have icall or intrinsic entry");
                var notImplementedMethodWriter = new NotImplementedMethodWriter(methodDetail, methodCtx.methodBodyCodeFile);
                notImplementedMethodWriter.WriteCode();
                return;
            }

            var methodWriter = new MethodWriter(methodDetail, methodCtx.methodBodyCodeFile);
            methodWriter.WriteCode();
        }


        private void GenerateVirtualMethodDefSelf(MethodDef methodDef, object ctx)
        {
            var methodCtx = (MethodDefGenerationContext)ctx;
            var methodDetail = GlobalServices.Inst.MetadataService.GetMethodDetail(methodDef);
            if (!methodDetail.ShouldGenerateVirtualMethod)
            {
                return;
            }
            var virtualMethodWriter = new VirtualMethodWriter(methodDetail, methodCtx.methodBodyCodeFile);
            virtualMethodWriter.WriteCode();
        }

        public override void GenerateGlobalInitializationCpp()
        {
            GlobalServices.Inst.ManifestService.CurrentHome = null;
            GenerateAllModuleRegistrationCpp();
            GenerateAllMethodInvokerCpp();
            GenerateAllDirectCallBridgeCpp();
        }

        private void GenerateAllModuleRegistrationCpp()
        {
            var allModuleRegistrationCppCodeFile = new ModulesRegistrationCppCodeFile($"{_config.outputCodeDir}/{ModuleGenerationUtil.GetAllModuleRegistrationCppFileNameWithExt()}", _config.manifest.AssemblyPlans.Values.Select(plan => plan.Module).ToList());
            allModuleRegistrationCppCodeFile.Generate();
            allModuleRegistrationCppCodeFile.Save();
        }

        private void GenerateAllMethodInvokerCpp()
        {
            int partIndex = 0;
            var invokerFiles = new List<MethodInvokerCodeFilePart>();
            var methodInvokerCodeFile = new MethodInvokerCodeFilePart($"{_config.outputCodeDir}/{ModuleGenerationUtil.GetMethodInvokerCppFileNameWithExt(partIndex++)}");
            invokerFiles.Add(methodInvokerCodeFile);
            var invokerService = GlobalServices.Inst.InvokerService;
            var methodInvokerInfos = invokerService.GetNotVirtualInvokers();
            methodInvokerInfos.Sort((a, b) => a.name.CompareTo(b.name));

            var virtualMethodInvokerInfos = invokerService.GetVirtualInvokers();
            virtualMethodInvokerInfos.Sort((a, b) => a.name.CompareTo(b.name));

            var totalMethodInvokerInfos = new List<MethodInvokerInfo>(methodInvokerInfos);
            totalMethodInvokerInfos.AddRange(virtualMethodInvokerInfos);

            foreach (var invokerInfo in totalMethodInvokerInfos)
            {
                if (methodInvokerCodeFile.ExceedsMaxSize())
                {
                    methodInvokerCodeFile = new MethodInvokerCodeFilePart($"{_config.outputCodeDir}/{ModuleGenerationUtil.GetMethodInvokerCppFileNameWithExt(partIndex++)}");
                    invokerFiles.Add(methodInvokerCodeFile);
                }
                methodInvokerCodeFile.AddInvokerImpl(invokerInfo);
            }
            foreach (var invokerFile in invokerFiles)
            {
                invokerFile.Save();
            }
        }

        private void GenerateAllDirectCallBridgeCpp()
        {
            int partIndex = 0;
            var directCallBridgeFiles = new List<MethodDirectCallBridgeCodeFilePart>();
            var directCallBridgeCodeFile = new MethodDirectCallBridgeCodeFilePart($"{_config.outputCodeDir}/{ModuleGenerationUtil.GetMethodDirectCallBridgeCppFileNameWithExt(partIndex++)}");
            directCallBridgeFiles.Add(directCallBridgeCodeFile);
            var directCallBridgeService = GlobalServices.Inst.DirectCallBridgeService;
            var directCallBridgeInfos = directCallBridgeService.GetDirectCallBridgeInfos();
            directCallBridgeInfos.Sort((a, b) => a.name.CompareTo(b.name));
            foreach (var directCallBridgeInfo in directCallBridgeInfos)
            {
                if (directCallBridgeCodeFile.ExceedsMaxSize())
                {
                    directCallBridgeCodeFile = new MethodDirectCallBridgeCodeFilePart($"{_config.outputCodeDir}/{ModuleGenerationUtil.GetMethodDirectCallBridgeCppFileNameWithExt(partIndex++)}");
                    directCallBridgeFiles.Add(directCallBridgeCodeFile);
                }
                directCallBridgeCodeFile.AddDirectCallBridgeImpl(directCallBridgeInfo);
            }
            foreach (var directCallBridgeFile in directCallBridgeFiles)
            {
                directCallBridgeFile.Save();
            }
        }
    }
}
