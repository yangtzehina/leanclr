// SPIKE: QCalls / P/Invokes the Linux x64 .NET 10 CoreLib needs that the (Windows-only) fork did not register.
#include "spike_linux_qcalls.h"

#include "vm/pinvoke.h"
#include "interp/eval_stack_op.h"

#include <cstring>

namespace leanclr
{
namespace pinvokes
{

// XplatEventLogger.EventSource_GetClrConfig(char*) -> char*: no CLR config store, return null ("" after new string(char*)).
static RtResultVoid xplat_get_clr_config_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject*,
                                                 interp::RtStackObject* ret) noexcept
{
    interp::EvalStackOp::set_return<intptr_t>(ret, 0);
    RET_VOID_OK();
}

// CustomAttribute_ParseAttributeUsageAttribute(byte* blob, int len, int* targets, int* allowMultiple, int* inherited)  (order = AttributeUsageAttribute ctor)
// blob = [AttributeUsage(AttributeTargets validOn, AllowMultiple = .., Inherited = ..)]; CoreCLR defaults Inherited=true, AllowMultiple=false
static RtResultVoid parse_attribute_usage_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                                  interp::RtStackObject* ret) noexcept
{
    const uint8_t* p = interp::EvalStackOp::get_param<const uint8_t*>(params, 0);
    int32_t len = interp::EvalStackOp::get_param<int32_t>(params, 1);
    int32_t* targets = interp::EvalStackOp::get_param<int32_t*>(params, 2);
    int32_t* inherited = interp::EvalStackOp::get_param<int32_t*>(params, 4);
    int32_t* allow_multiple = interp::EvalStackOp::get_param<int32_t*>(params, 3);
    interp::EvalStackOp::set_return<int32_t>(ret, 1); // success (wrapper tests result != 0); ret aliases params, so only after reading them
    *targets = 0;
    *inherited = 1;
    *allow_multiple = 0;
    const uint8_t* end = p + len;
    if (len < 8 || p[0] != 1 || p[1] != 0)
        RET_VOID_OK();
    p += 2;
    *targets = static_cast<int32_t>(p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<uint32_t>(p[3]) << 24));
    p += 4;
    uint32_t named = static_cast<uint32_t>(p[0] | (p[1] << 8));
    p += 2;
    for (uint32_t i = 0; i < named && p + 3 <= end; i++)
    {
        uint8_t kind = *p++;  // 0x53 field / 0x54 property
        uint8_t type = *p++;  // 0x02 boolean
        (void)kind;
        uint32_t nlen = *p++; // SerString, names are short
        if (p + nlen + 1 > end || type != 0x02)
            break;
        bool is_inherited = nlen == 9 && std::memcmp(p, "Inherited", 9) == 0;
        bool is_allow = nlen == 13 && std::memcmp(p, "AllowMultiple", 13) == 0;
        p += nlen;
        uint8_t v = *p++;
        if (is_inherited)
            *inherited = v != 0;
        else if (is_allow)
            *allow_multiple = v != 0;
    }
    RET_VOID_OK();
}

void register_spike_linux_qcalls() noexcept
{
    vm::PInvokes::register_pinvoke(
        "System.Reflection.CustomAttribute::ParseAttributeUsageAttribute(System.IntPtr,System.Int32,System.Int32*,System.Int32*,System.Int32*)", nullptr,
        parse_attribute_usage_invoker);
    vm::PInvokes::register_pinvoke("System.Diagnostics.Tracing.XplatEventLogger::<EventSource_GetClrConfig>g____PInvoke", nullptr,
                                   xplat_get_clr_config_invoker);
}

} // namespace pinvokes
} // namespace leanclr
