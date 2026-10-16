using dnlib.DotNet;
using LeanAOT.GenerationPlan;

namespace LeanAOT.ToCpp
{
    public class ManifestService
    {
        public Manifest Manifest { get; }

        /// <summary>The module whose files are being generated: code there may call an instantiation directly only when
        /// the instantiation's home binding is covered by this module's (see <see cref="Manifest.ShouldAOT(IMethod, string)"/>).</summary>
        public string CurrentHome { get; set; }

        public ManifestService(Manifest manifest)
        {
            Manifest = manifest;
        }

        public bool ShouldAOT(IMethod method)
        {
            return Manifest.ShouldAOT(method, CurrentHome);
        }

        public GenericMethodPlan TryGetGenericPlan(IMethod method)
        {
            return Manifest.TryGetGenericPlan(method);
        }
    }
}
