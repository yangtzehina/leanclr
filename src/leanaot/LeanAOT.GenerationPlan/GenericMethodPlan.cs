using dnlib.DotNet;
using LeanAOT.Core;

namespace LeanAOT.GenerationPlan
{
    /// <summary>One closed generic instantiation compiled to C++ (see <see cref="GenericPlanner"/>).</summary>
    public class GenericMethodPlan
    {
        public GenericInstantiation Inst { get; set; }

        public IMethod Method => Inst.Method;

        /// <summary>The AOT module whose generated code holds this instantiation: of the modules whose reference closure
        /// contains every assembly the instantiation involves (<see cref="GenericInstantiation.InvolvedAssemblies"/>), the
        /// one with the smallest closure. Its MVID binding then covers every layout and token the code embeds.</summary>
        public string Home { get; set; }

        /// <summary>Why it is compiled: "static" (reached from compiled code), "virtual" (an override on a type that
        /// compiled code constructs), "comparer" (the default comparer the runtime will create), "profile" (ran in
        /// the profiled replay, runtime/aot/generics-hot.txt).</summary>
        public string Origin { get; set; }
    }
}
