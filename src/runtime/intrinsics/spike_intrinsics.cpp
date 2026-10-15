// SPIKE: CoreLib [Intrinsic] placeholders whose IL body is self-recursive (the JIT always expands them).
#include "spike_intrinsics.h"

#include <cmath>
#include <cstdint>

#include "interp/eval_stack_op.h"

namespace leanclr
{
namespace intrinsics
{
using interp::EvalStackOp;

static const metadata::RtTypeSig* method_generic_arg0(const metadata::RtMethodInfo* method) noexcept
{
    if (method == nullptr || method->generic_method == nullptr || method->generic_method->generic_context.method_inst == nullptr)
        return nullptr;
    return method->generic_method->generic_context.method_inst->generic_args[0];
}

// x64 CoreCLR semantics of ConvertToIntegerNative<T>: cvttsd2si (32-bit for <= 32-bit signed targets, 64-bit otherwise),
// out of range / NaN -> "integer indefinite" (0x80000000 / 0x8000000000000000), then truncation to T.
static int32_t cvt_i32(double d) noexcept
{
    return (d >= -2147483648.0 && d < 2147483648.0) ? static_cast<int32_t>(d) : INT32_MIN;
}
static int64_t cvt_i64(double d) noexcept
{
    return (d >= -9223372036854775808.0 && d < 9223372036854775808.0) ? static_cast<int64_t>(d) : INT64_MIN;
}

static RtResultVoid convert_native(const metadata::RtMethodInfo* method, double d, interp::RtStackObject* ret) noexcept
{
    const metadata::RtTypeSig* t = method_generic_arg0(method);
    if (t == nullptr)
        RET_ERR(RtErr::ExecutionEngine);
    switch (t->ele_type)
    {
    case metadata::RtElementType::I1:
        EvalStackOp::set_return<int32_t>(ret, static_cast<int8_t>(cvt_i32(d)));
        break;
    case metadata::RtElementType::U1:
        EvalStackOp::set_return<int32_t>(ret, static_cast<uint8_t>(cvt_i32(d)));
        break;
    case metadata::RtElementType::I2:
        EvalStackOp::set_return<int32_t>(ret, static_cast<int16_t>(cvt_i32(d)));
        break;
    case metadata::RtElementType::U2:
    case metadata::RtElementType::Char:
        EvalStackOp::set_return<int32_t>(ret, static_cast<uint16_t>(cvt_i32(d)));
        break;
    case metadata::RtElementType::I4:
        EvalStackOp::set_return<int32_t>(ret, cvt_i32(d));
        break;
    case metadata::RtElementType::U4:
        EvalStackOp::set_return<int32_t>(ret, static_cast<int32_t>(static_cast<uint32_t>(cvt_i64(d))));
        break;
    case metadata::RtElementType::I8:
    case metadata::RtElementType::I:
        EvalStackOp::set_return<int64_t>(ret, cvt_i64(d));
        break;
    case metadata::RtElementType::U8:
    case metadata::RtElementType::U:
    {
        uint64_t r;
        if (d >= 0.0 && d < 9223372036854775808.0)
            r = static_cast<uint64_t>(d);
        else if (d >= 9223372036854775808.0 && d < 18446744073709551616.0)
            r = static_cast<uint64_t>(d);
        else
            r = 0x8000000000000000ull;
        EvalStackOp::set_return<uint64_t>(ret, r);
        break;
    }
    default:
        RET_ERR(RtErr::ExecutionEngine);
    }
    RET_VOID_OK();
}

static RtResultVoid double_convert_native_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo* method, const interp::RtStackObject* params,
                                                  interp::RtStackObject* ret) noexcept
{
    return convert_native(method, EvalStackOp::get_param<double>(params, 0), ret);
}

static RtResultVoid single_convert_native_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo* method, const interp::RtStackObject* params,
                                                  interp::RtStackObject* ret) noexcept
{
    return convert_native(method, static_cast<double>(EvalStackOp::get_param<float>(params, 0)), ret);
}

static RtResultVoid double_mul_add_estimate_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                                    interp::RtStackObject* ret) noexcept
{
    EvalStackOp::set_return<double>(ret, std::fma(EvalStackOp::get_param<double>(params, 0), EvalStackOp::get_param<double>(params, 1),
                                                  EvalStackOp::get_param<double>(params, 2)));
    RET_VOID_OK();
}

static RtResultVoid single_mul_add_estimate_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                                    interp::RtStackObject* ret) noexcept
{
    EvalStackOp::set_return<float>(ret, std::fma(EvalStackOp::get_param<float>(params, 0), EvalStackOp::get_param<float>(params, 1),
                                                 EvalStackOp::get_param<float>(params, 2)));
    RET_VOID_OK();
}

static RtResultVoid math_recip_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                       interp::RtStackObject* ret) noexcept
{
    EvalStackOp::set_return<double>(ret, 1.0 / EvalStackOp::get_param<double>(params, 0));
    RET_VOID_OK();
}

static RtResultVoid math_rsqrt_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                       interp::RtStackObject* ret) noexcept
{
    EvalStackOp::set_return<double>(ret, 1.0 / std::sqrt(EvalStackOp::get_param<double>(params, 0)));
    RET_VOID_OK();
}

static RtResultVoid mathf_recip_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                        interp::RtStackObject* ret) noexcept
{
    EvalStackOp::set_return<float>(ret, 1.0f / EvalStackOp::get_param<float>(params, 0));
    RET_VOID_OK();
}

static RtResultVoid mathf_rsqrt_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject* params,
                                        interp::RtStackObject* ret) noexcept
{
    EvalStackOp::set_return<float>(ret, 1.0f / std::sqrt(EvalStackOp::get_param<float>(params, 0)));
    RET_VOID_OK();
}

static RtResultVoid nop_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo*, const interp::RtStackObject*, interp::RtStackObject*) noexcept
{
    RET_VOID_OK();
}

static vm::IntrinsicEntry s_spike_intrinsics[] = {
    {"System.Double::ConvertToIntegerNative<>(System.Double)", nullptr, double_convert_native_invoker},
    {"System.Single::ConvertToIntegerNative<>(System.Single)", nullptr, single_convert_native_invoker},
    {"System.Double::MultiplyAddEstimate(System.Double,System.Double,System.Double)", nullptr, double_mul_add_estimate_invoker},
    {"System.Single::MultiplyAddEstimate(System.Single,System.Single,System.Single)", nullptr, single_mul_add_estimate_invoker},
    {"System.Math::ReciprocalEstimate(System.Double)", nullptr, math_recip_invoker},
    {"System.Math::ReciprocalSqrtEstimate(System.Double)", nullptr, math_rsqrt_invoker},
    {"System.MathF::ReciprocalEstimate(System.Single)", nullptr, mathf_recip_invoker},
    {"System.MathF::ReciprocalSqrtEstimate(System.Single)", nullptr, mathf_rsqrt_invoker},
    {"System.Threading.Volatile::ReadBarrier()", nullptr, nop_invoker},
    {"System.Threading.Volatile::WriteBarrier()", nullptr, nop_invoker},
};

utils::Span<vm::IntrinsicEntry> SpikeIntrinsics::get_intrinsic_entries() noexcept
{
    return utils::Span<vm::IntrinsicEntry>(s_spike_intrinsics, sizeof(s_spike_intrinsics) / sizeof(vm::IntrinsicEntry));
}

} // namespace intrinsics
} // namespace leanclr
