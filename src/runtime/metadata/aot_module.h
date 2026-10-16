#pragma once
#include "core/stl_compat.h"

#include "module_def.h"

namespace leanclr
{
namespace metadata
{
typedef RtInvokeMethodPointer AotInvoker;

class AotModule
{
  public:
    static void register_aot_modules(const RtAotModulesData* initData);
    static const RtAotModuleData* find_aot_module_by_name(const char* module_name);
    static const RtAotMethodDefData* find_aot_method_impl(const RtMethodInfo* method);
    static const RtAotMethodDefData* find_aot_method_def_impl(const RtModuleDef* module, EncodedTokenId token);
    static const RtAotMethodMonoPInvokeCallbackData* find_mono_pinvoke_callback_method(const RtModuleDef* module, EncodedTokenId token);
    static RtResult<const RtMethodInfo*> find_mono_pinvoke_callback_method_by_native_ptr(RtNativeMethodPointer ptr);

    // AOT code is bound to the exact images it was generated from (RtAotModuleData::bindings). Called when `mod` has
    // been loaded together with its reference closure: Ok(true) = attach, Ok(false) = run the module interpreted
    // (policy Interpret), Err = refuse to load it (policy Strict).
    static RtResult<bool> check_bindings(const RtModuleDef* mod, const RtAotModuleData* aot_data);
    static void set_binding_policy(AotBindingPolicy policy);
    static AotBindingPolicy get_binding_policy();
    static uint32_t get_attached_module_count();
    static uint32_t get_attached_method_count();
    static uint32_t get_rejected_module_count();
    // "" or the reason the last module was rejected, e.g. "ET.Hotfix: ET.Model MVID 0123.. != generated 4567.."
    static const char* get_last_rejection();
    static void note_attached(const RtAotModuleData* aot_data);

    // Generic instantiations: the key of a closed inflated method, computed from runtime type data only (assembly names,
    // TypeDef / MethodDef row ids, element types); LeanAOT computes the same string from the DLLs (GenericInstantiation
    // .RidKey). False when the method is not a closed instantiation or the key does not fit.
    static bool format_generic_method_key(const RtMethodInfo* method, char* buf, size_t size);
    static uint32_t get_attached_generic_method_count();
};
} // namespace metadata
} // namespace leanclr
