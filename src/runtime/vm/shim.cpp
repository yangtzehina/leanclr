#include "shim.h"
#include "interp/machine_state.h"
#include <cstdio>
#include <cstdlib>
#include "field.h"
#include "method.h"
#include "rt_array.h"
#include "class.h"
#include "delegate.h"
#include "internal_calls.h"
#include "intrinsics.h"
#include "pinvoke.h"
#include "const_strs.h"
#include "method.h"
#include "interp/interpreter.h"
#include "utils/string_builder.h"
#include "metadata/metadata_name.h"
#include "metadata/module_def.h"
#include "metadata/aot_module.h"

namespace leanclr
{
namespace vm
{

// Static function pointers for invokers
namespace
{

// Interpreter invoker
RtResultVoid fn_interpreter_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method, const interp::RtStackObject* params,
                                    interp::RtStackObject* ret) noexcept
{
    if (method_pointer != method->method_ptr)
    {
        assert(Class::is_value_type(method->parent));
        assert(method_pointer == method->virtual_method_ptr);
        const_cast<interp::RtStackObject*>(params)[0].obj = params[0].obj + 1;
    }
    auto execute_ret = interp::Interpreter::execute(method, params);
    if (execute_ret.is_err())
    {
        RET_ERR(execute_ret.unwrap_err());
    }
    const interp::RtStackObject* result = execute_ret.unwrap();
    if (method->ret_stack_object_size > 0)
    {
        std::memcpy(ret, result, method->ret_stack_object_size * sizeof(interp::RtStackObject));
    }
    RET_VOID_OK();
}

bool should_skip_legacy_il_internal_call(const metadata::RtMethodInfo* method) noexcept
{
    const metadata::RtClass* klass = method->parent;
    if (!klass->image->is_corlib())
    {
        return false;
    }

    if (std::strcmp(klass->namespaze, "System") == 0 && std::strcmp(klass->name, "Object") == 0 &&
        std::strcmp(method->name, "GetType") == 0 && method->parameter_count == 0)
    {
        return true;
    }

    if (std::strcmp(klass->image->get_name_no_ext(), STR_SYSTEM_PRIVATE_CORELIB_NAME) == 0 &&
        std::strcmp(klass->name, "Kernel32") == 0)
    {
        return std::strcmp(method->name, "CloseHandle") == 0 ||
               std::strcmp(method->name, "CreateFilePrivate") == 0 ||
               std::strcmp(method->name, "DeleteFilePrivate") == 0 ||
               std::strcmp(method->name, "GetFileAttributesExPrivate") == 0 ||
               std::strcmp(method->name, "GetFileInformationByHandle") == 0 ||
               std::strcmp(method->name, "GetFileInformationByHandleEx") == 0 ||
               std::strcmp(method->name, "ReadFile") == 0 ||
               std::strcmp(method->name, "SetFilePointerEx") == 0 ||
               std::strcmp(method->name, "WriteFile") == 0;
    }

    return std::strcmp(klass->namespaze, "System.Reflection") == 0 && std::strcmp(klass->name, "MethodBase") == 0 &&
           std::strcmp(method->name, "GetCurrentMethod") == 0 && method->parameter_count == 0;
}

// Not implemented internal call invoker
RtResultVoid fn_not_implemented_internal_call_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method,
                                                      const interp::RtStackObject* params, interp::RtStackObject* ret) noexcept
{
    printf("Internal call invoker not implemented for method: %s.%s::%s token:0x%0x\n", method->parent->namespaze, method->parent->name,
           method->name, method->token);
    RETURN_NOT_IMPLEMENTED_ERROR();
}

// Not implemented intrinsic invoker
RtResultVoid fn_not_implemented_intrinsic_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method,
                                                  const interp::RtStackObject* params, interp::RtStackObject* ret) noexcept
{
    printf("Intrinsic invoker not implemented for method: %s.%s::%s token:0x%0x\n", method->parent->namespaze, method->parent->name, method->name,
           method->token);
    RETURN_NOT_IMPLEMENTED_ERROR();
}

// PInvoke invoker (not implemented)
RtResultVoid fn_pinvoke_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method, const interp::RtStackObject* params,
                                interp::RtStackObject* ret) noexcept
{
    printf("P/Invoke invoker not implemented for method: %s.%s::%s token:0x%0x\n", method->parent->namespaze, method->parent->name, method->name,
           method->token);
    RETURN_NOT_IMPLEMENTED_ERROR();
}

static void spike_dump_frames(const char* why)
{
    if (std::getenv("LEANCLR_THROW_TRACE") == nullptr)
        return;
    std::fprintf(stderr, "leanclr-%s\n", why);
    auto frames = interp::MachineState::get_global_machine_state().get_active_frames();
    for (size_t i = frames.size(); i-- > 0 && frames.size() - i <= 24;)
    {
        const metadata::RtMethodInfo* m = frames[i].method;
        std::fprintf(stderr, "    at %s.%s::%s\n", m && m->parent ? m->parent->namespaze : "?", m && m->parent ? m->parent->name : "?", m ? m->name : "?");
    }
}

// Not implemented PInvoke invoker
RtResultVoid fn_not_implemented_pinvoke_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method,
                                                const interp::RtStackObject* params, interp::RtStackObject* ret) noexcept
{
    printf("P/Invoke invoker not implemented for method: %s.%s::%s token:0x%0x\n", method->parent->namespaze, method->parent->name, method->name,
           method->token);
    spike_dump_frames("pinvoke-not-implemented");
    RETURN_NOT_IMPLEMENTED_ERROR();
}

// Not implemented runtime impl invoker
RtResultVoid fn_not_implemented_runtime_impl_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method,
                                                     const interp::RtStackObject* params, interp::RtStackObject* ret) noexcept
{
    printf("Runtime impl invoker not implemented for method: %s.%s::%s token:0x%0x\n", method->parent->namespaze, method->parent->name,
           method->name, method->token);
    RETURN_NOT_IMPLEMENTED_ERROR();
}

// Not implemented generic invoker
RtResultVoid fn_not_implemented_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method,
                                        const interp::RtStackObject* params, interp::RtStackObject* ret) noexcept
{
    printf("Generic invoker not implemented for method: %s.%s::%s token:0x%0x\n", method->parent->namespaze, method->parent->name, method->name,
           method->token);
    RETURN_NOT_IMPLEMENTED_ERROR();
}

bool is_array_interface_count_method(const char* method_name) noexcept
{
    return std::strcmp(method_name, "get_Count") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.ICollection`1.get_Count") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.IReadOnlyCollection`1.get_Count") == 0;
}

bool is_array_interface_get_item_method(const char* method_name) noexcept
{
    return std::strcmp(method_name, "get_Item") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.IList`1.get_Item") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.IReadOnlyList`1.get_Item") == 0;
}

bool is_array_interface_set_item_method(const char* method_name) noexcept
{
    return std::strcmp(method_name, "set_Item") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.IList`1.set_Item") == 0;
}

bool is_array_interface_copy_to_method(const char* method_name) noexcept
{
    return std::strcmp(method_name, "CopyTo") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.ICollection`1.CopyTo") == 0;
}

bool is_array_interface_get_enumerator_method(const char* method_name) noexcept
{
    return std::strcmp(method_name, "GetEnumerator") == 0 ||
           std::strcmp(method_name, "System.Collections.Generic.IEnumerable`1.GetEnumerator") == 0;
}

// Method pointer placeholder
void fn_not_implemented_method_pointer() noexcept
{
    // Placeholder - would panic in debug builds
}

void fn_not_implemented_virtual_method_pointer() noexcept
{
    // Placeholder - would panic in debug builds
}

} // anonymous namespace

// Helper function to try setting up array or szarray invokers
static metadata::RtInvokeMethodPointer try_setup_array_or_szarray_invoke(const metadata::RtMethodInfo* method)
{
    const metadata::RtClass* klass = method->parent;
    const char* method_name = method->name;
    size_t param_count = method->parameter_count;

    if (Class::is_szarray_class(klass))
    {
        // Single-dimensional array methods
        if (std::strcmp(method_name, STR_CTOR) == 0)
        {
            return Array::szarray_new_invoker;
        }
        else if (std::strcmp(method_name, STR_ARRAY_GET_NZ) == 0)
        {
            assert(param_count == 1);
            return Array::szarray_get_invoker;
        }
        else if (std::strcmp(method_name, STR_ARRAY_SET_NZ) == 0)
        {
            assert(param_count == 2);
            return Array::szarray_set_invoker;
        }
        else if (std::strcmp(method_name, STR_ARRAY_ADDRESS_NZ) == 0)
        {
            assert(param_count == 1);
            return Array::szarray_address_invoker;
        }
        else if (is_array_interface_count_method(method_name))
        {
            assert(param_count == 0);
            return Array::szarray_interface_count_invoker;
        }
        else if (is_array_interface_get_item_method(method_name))
        {
            assert(param_count == 1);
            return Array::szarray_get_invoker;
        }
        else if (is_array_interface_set_item_method(method_name))
        {
            assert(param_count == 2);
            return Array::szarray_set_invoker;
        }
        else if (is_array_interface_copy_to_method(method_name))
        {
            assert(param_count == 2);
            return Array::szarray_interface_copy_to_invoker;
        }
        else if (is_array_interface_get_enumerator_method(method_name))
        {
            assert(param_count == 0);
            return Array::szarray_generic_get_enumerator_invoker;
        }
        else
        {
            return nullptr;
        }
    }
    else
    {
        // Multi-dimensional array methods
        uint8_t rank = klass->by_val->data.array_type->rank;

        if (std::strcmp(method_name, STR_CTOR) == 0)
        {
            assert(param_count == rank || param_count == rank * 2);
            if (param_count == rank)
            {
                return Array::newmdarray_lengths_invoker;
            }
            else
            {
                return Array::newmdarray_lengths_lower_bounds_invoker;
            }
        }
        else if (std::strcmp(method_name, STR_ARRAY_GET_NZ) == 0)
        {
            assert(param_count == rank);
            return Array::mdarray_get_invoker;
        }
        else if (std::strcmp(method_name, STR_ARRAY_SET_NZ) == 0)
        {
            assert(param_count == rank + 1);
            return Array::mdarray_set_invoker;
        }
        else if (std::strcmp(method_name, STR_ARRAY_ADDRESS_NZ) == 0)
        {
            assert(param_count == rank);
            return Array::mdarray_address_invoker;
        }
        else
        {
            return nullptr;
        }
    }
}

// Get invoker for a method
// ---- 32-bit QCall argument widening (wasm32 / arm32) ----------------------------------------------------------
// The hand-written CoreCLR QCall invokers (pinvokes/coreclr_qcall.cpp, ~600 get_param sites) index their arguments as
// if every pointer-sized field of a struct argument owned its own 8-byte eval-stack slot. That holds on 64-bit
// (QCallTypeHandle {void* _ptr; nint _handle} = 16 bytes = 2 slots) but not on 32-bit, where the same struct is 8 bytes
// = 1 slot and every later index is shifted. Instead of rewriting each invoker, re-lay the arguments into the 64-bit
// shape: a value-type argument made only of pointer-sized fields gets one zero-extended slot per field.
static bool is_pointer_sized_field_sig(const metadata::RtTypeSig* t)
{
    if (t->is_by_ref())
        return true;
    switch (t->ele_type)
    {
    case metadata::RtElementType::I:
    case metadata::RtElementType::U:
    case metadata::RtElementType::Ptr:
    case metadata::RtElementType::FnPtr:
    case metadata::RtElementType::Class:
    case metadata::RtElementType::Object:
    case metadata::RtElementType::String:
    case metadata::RtElementType::SZArray:
    case metadata::RtElementType::Array:
        return true;
    default:
        return false;
    }
}

// number of pointer-sized instance fields if the struct is made only of them (and has >= 2), else 0
static uint32_t pointer_struct_field_count(const metadata::RtTypeSig* t)
{
    if (t->is_by_ref() || (t->ele_type != metadata::RtElementType::ValueType && t->ele_type != metadata::RtElementType::GenericInst))
        return 0;
    auto kr = Class::get_class_from_typesig(t);
    if (kr.is_err())
        return 0;
    metadata::RtClass* klass = kr.unwrap();
    if (!Class::is_value_type(klass) || Class::is_enum_type(klass))
        return 0;
    if (Class::initialize_fields(klass).is_err())
        return 0;
    uint32_t n = 0;
    for (uint16_t i = 0; i < klass->field_count; ++i)
    {
        const metadata::RtFieldInfo* f = &klass->fields[i];
        if (Field::is_static_included_literal_and_rva(f))
            continue;
        if (!is_pointer_sized_field_sig(f->type_sig))
            return 0;
        ++n;
    }
    return n >= 2 ? n : 0;
}

struct Widen32Plan
{
    metadata::RtInvokeMethodPointer inner;
    bool needed;
};
static utils::HashMap<const metadata::RtMethodInfo*, Widen32Plan> s_widen32_plans;

static RtResultVoid fn_pinvoke_widen32_invoker(metadata::RtManagedMethodPointer method_pointer, const metadata::RtMethodInfo* method,
                                               const interp::RtStackObject* params, interp::RtStackObject* ret) noexcept
{
    const Widen32Plan& plan = s_widen32_plans[method];
    if (!plan.needed)
        return plan.inner(method_pointer, method, params, ret);
    interp::RtStackObject widened[64];
    size_t out = 0;
    size_t in = 0;
    for (uint16_t i = 0; i < method->parameter_count; ++i)
    {
        const metadata::RtTypeSig* t = method->parameters[i];
        uint32_t nf = pointer_struct_field_count(t);
        size_t slots = method->arg_descs != nullptr ? method->arg_descs[i].stack_object_size : 1;
        if (nf != 0)
        {
            const uint32_t* src = reinterpret_cast<const uint32_t*>(params + in);
            for (uint32_t k = 0; k < nf && out < 64; ++k)
            {
                widened[out++].value = static_cast<uint64_t>(src[k]);
            }
        }
        else
        {
            for (size_t k = 0; k < slots && out < 64; ++k)
                widened[out++] = params[in + k];
        }
        in += slots;
    }
    return plan.inner(method_pointer, method, widened, ret);
}

static metadata::RtInvokeMethodPointer maybe_widen32(const metadata::RtMethodInfo* method, metadata::RtInvokeMethodPointer inner)
{
    if (sizeof(void*) != 4 || inner == nullptr)
        return inner;
    bool needed = false;
    for (uint16_t i = 0; i < method->parameter_count; ++i)
    {
        if (pointer_struct_field_count(method->parameters[i]) != 0)
        {
            needed = true;
            break;
        }
    }
    if (!needed)
        return inner;
    s_widen32_plans[method] = Widen32Plan{inner, true};
    return fn_pinvoke_widen32_invoker;
}

RtResult<InvokeTypeAndMethod> Shim::get_invoker(const metadata::RtMethodInfo* method)
{
    const metadata::RtClass* klass = method->parent;

    // Check for array/szarray methods
    if (Class::is_array_or_szarray(klass))
    {
        if (auto result = try_setup_array_or_szarray_invoke(method))
        {
            RET_OK(InvokeTypeAndMethod(RtInvokerType::CustomInstrinsic, result));
        }
    }

    // Determine invoker based on method implementation type
    metadata::RtMethodImplAttribute code_type = Method::get_code_type(method);

    if (Method::is_pinvoke(method))
    {
        DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL3(const PInvokeRegistry*, entry, PInvokes::get_pinvoke_by_method(method));
        if (entry)
        {
            RET_OK(InvokeTypeAndMethod(RtInvokerType::PInvoke, maybe_widen32(method, entry->invoker)));
        }

        RET_OK(InvokeTypeAndMethod(RtInvokerType::PInvoke, fn_not_implemented_pinvoke_invoker));
    }

    switch (code_type)
    {
    case metadata::RtMethodImplAttribute::IlOrManaged:
    {
        // Try internal call first (the lookup is pre-filtered by name and cheap when nothing matches, so the
        // string comparisons of the legacy exceptions only run for the few methods that have an entry)

        {
            DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL3(const InternalCallRegistry*, icall_entry, InternalCalls::get_internal_call_by_method(method));
            if (icall_entry && !should_skip_legacy_il_internal_call(method))
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::InternalCall, icall_entry->invoker));
            }
        }

        // Try intrinsic

        DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL3(const IntrinsicRegistry*, intrinsic_entry, Intrinsics::get_intrinsic_by_method(method));
        if (intrinsic_entry)
        {
            RET_OK(InvokeTypeAndMethod(RtInvokerType::Intrinsic, intrinsic_entry->invoker));
        }

        const metadata::RtAotMethodDefData* aot_data = metadata::AotModule::find_aot_method_impl(method);
        if (aot_data != nullptr)
        {
            RET_OK(InvokeTypeAndMethod(RtInvokerType::Aot, aot_data->invoke_method_ptr));
        }

        if (Method::is_internal_call(method))
        {
            RET_OK(InvokeTypeAndMethod(RtInvokerType::InternalCall, fn_not_implemented_internal_call_invoker));
        }

        // DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL3(bool, is_intrinsic_method, Method::is_intrinsic(method));
        // if (is_intrinsic_method)
        // {
        //     RET_OK(InvokeTypeAndMethod(RtInvokerType::Intrinsic, fn_not_implemented_intrinsic_invoker));
        // }

        if (Method::is_pinvoke(method))
        {
            DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL3(const PInvokeRegistry*, entry, PInvokes::get_pinvoke_by_method(method));
            if (entry)
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::PInvoke, maybe_widen32(method, entry->invoker)));
            }
            else
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::PInvoke, fn_not_implemented_pinvoke_invoker));
            }
        }
        RET_OK(InvokeTypeAndMethod(RtInvokerType::Interpreter, fn_interpreter_invoker));
    }

    case metadata::RtMethodImplAttribute::Native:
        RET_OK(InvokeTypeAndMethod(RtInvokerType::NotImplemented, fn_not_implemented_invoker));

    case metadata::RtMethodImplAttribute::Optil:
        RET_OK(InvokeTypeAndMethod(RtInvokerType::NotImplemented, fn_not_implemented_invoker));

    case metadata::RtMethodImplAttribute::Runtime:
    {
        // Check if it's a delegate method
        if (Class::is_multicastdelegate_subclass(klass))
        {
            const char* method_name = method->name;

            if (std::strcmp(method_name, STR_CTOR) == 0)
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::RuntimeImpl, Delegate::call_delegate_ctor_invoker));
            }
            else if (std::strcmp(method_name, STR_INVOKE) == 0)
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::RuntimeImpl, Delegate::invoke_delegate_invoker));
            }
            else if (std::strcmp(method_name, STR_BEGININVOKE) == 0)
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::RuntimeImpl, Delegate::begin_invoke_delegate_invoker));
            }
            else if (std::strcmp(method_name, STR_ENDINVOKE) == 0)
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::RuntimeImpl, Delegate::end_invoke_delegate_invoker));
            }
            else
            {
                RET_OK(InvokeTypeAndMethod(RtInvokerType::RuntimeImpl, fn_not_implemented_runtime_impl_invoker));
            }
        }
        else
        {
            RET_OK(InvokeTypeAndMethod(RtInvokerType::RuntimeImpl, fn_not_implemented_runtime_impl_invoker));
        }
    }

    default:
        RET_OK(InvokeTypeAndMethod(RtInvokerType::NotImplemented, fn_not_implemented_invoker));
    }
}

// Get method pointer (for native/unmanaged methods)
MethodAndVirtualMethod Shim::get_method_pointer(const metadata::RtMethodInfo* method)
{
    // generic instantiations are registered by key, from whichever module covers the assemblies they involve (not
    // necessarily the declaring one, e.g. List<ET.Entity> lives in an ET module), so they skip the image check
    const metadata::RtAotModuleData* aotModuleData = method->parent->image->get_aot_module_data();
    if (aotModuleData != nullptr || method->generic_method != nullptr)
    {
        const metadata::RtAotMethodDefData* aot_data = metadata::AotModule::find_aot_method_impl(method);
        if (aot_data != nullptr)
        {
            return MethodAndVirtualMethod(aot_data->method_ptr, aot_data->virtual_method_ptr);
        }
    }
    metadata::RtManagedMethodPointer method_pointer = reinterpret_cast<metadata::RtManagedMethodPointer>(fn_not_implemented_method_pointer);
    metadata::RtManagedMethodPointer virtual_method_pointer = reinterpret_cast<metadata::RtManagedMethodPointer>(
        Method::is_static(method) || !Class::is_value_type(method->parent) ? fn_not_implemented_method_pointer : fn_not_implemented_virtual_method_pointer);

    return MethodAndVirtualMethod(method_pointer, virtual_method_pointer);
}

} // namespace vm
} // namespace leanclr
