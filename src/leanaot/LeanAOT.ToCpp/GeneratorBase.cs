using LeanAOT.GenerationPlan;

namespace LeanAOT.ToCpp
{

    public abstract class GeneratorBase : IGenerator
    {
        protected GenerationConfig _config;

        public void Generate(GenerationConfig conf)
        {
            _config = conf;
            Prepare();
            foreach (var assPlan in conf.manifest.AssemblyPlans.Values)
            {
                GenerateAssembly(assPlan);
            }
            GenerateGlobalInitializationCpp();
        }

        /// <summary>Runs once before any assembly is generated.</summary>
        protected virtual void Prepare()
        {
        }

        public abstract void GenerateAssembly(AssemblyPlan plan);

        public abstract void GenerateMethodDef(MethodDefPlan plan, object ctx);

        public abstract void GenerateGlobalInitializationCpp();
    }
}
