// SPIKE: generic bridge from blittable P/Invokes (LibraryImport inner stubs) to real native libraries
// (libSystem.Native.so etc. from the .NET shared framework) on non-Windows native hosts.
// Only integer / pointer parameters and returns (<= 8 params), which covers System.Native's surface.
// wasm would link the same System.Native sources statically instead of dlopen (browser-wasm does that).
#include "native_bridge.h"

#include "build_config.h"
#include "metadata/module_def.h"
#include "interp/interp_defs.h"
#include "interp/eval_stack_op.h"

#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>

#if !LEANCLR_PLATFORM_WIN && !defined(__EMSCRIPTEN__)
#include <dlfcn.h>
#define LEANCLR_SPIKE_NATIVE_BRIDGE 1
#endif

namespace leanclr
{
namespace vm
{
#if LEANCLR_SPIKE_NATIVE_BRIDGE
using metadata::RtArgOrLocOrFieldReduceType;

struct BridgeEntry
{
    PInvokeRegistry reg;
    void* fn;
    uint16_t argc;
    RtArgOrLocOrFieldReduceType arg_type[8];
    uint16_t arg_slots[8];
    RtArgOrLocOrFieldReduceType ret_type;
};

static std::unordered_map<const metadata::RtMethodInfo*, BridgeEntry*> g_bridge;
static std::unordered_map<std::string, void*> g_libs;

static void* open_lib(const char* name)
{
    auto it = g_libs.find(name);
    if (it != g_libs.end())
        return it->second;
    const char* dir = std::getenv("LEANCLR_NATIVE_LIB_DIR");
    std::string d = dir ? dir : "/usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.12";
    std::string n = name;
    if (n.size() < 3 || n.compare(n.size() - 3, 3, ".so") != 0)
        n += ".so";
    void* h = dlopen((d + "/" + n).c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!h)
        h = dlopen(n.c_str(), RTLD_NOW | RTLD_LOCAL);
    g_libs[name] = h;
    return h;
}

static bool supported(RtArgOrLocOrFieldReduceType t)
{
    switch (t)
    {
    case RtArgOrLocOrFieldReduceType::I1:
    case RtArgOrLocOrFieldReduceType::U1:
    case RtArgOrLocOrFieldReduceType::I2:
    case RtArgOrLocOrFieldReduceType::U2:
    case RtArgOrLocOrFieldReduceType::I4:
    case RtArgOrLocOrFieldReduceType::I8:
    case RtArgOrLocOrFieldReduceType::I:
        return true;
    default:
        return false;
    }
}

static int64_t load_arg(RtArgOrLocOrFieldReduceType t, const interp::RtStackObject* p)
{
    switch (t)
    {
    case RtArgOrLocOrFieldReduceType::I1:
        return *reinterpret_cast<const int8_t*>(p);
    case RtArgOrLocOrFieldReduceType::U1:
        return *reinterpret_cast<const uint8_t*>(p);
    case RtArgOrLocOrFieldReduceType::I2:
        return *reinterpret_cast<const int16_t*>(p);
    case RtArgOrLocOrFieldReduceType::U2:
        return *reinterpret_cast<const uint16_t*>(p);
    case RtArgOrLocOrFieldReduceType::I4:
        return *reinterpret_cast<const int32_t*>(p);
    default:
        return *reinterpret_cast<const int64_t*>(p);
    }
}

using Fn8 = int64_t (*)(int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t, int64_t);

static RtResultVoid bridge_invoker(metadata::RtManagedMethodPointer, const metadata::RtMethodInfo* method, const interp::RtStackObject* params,
                                   interp::RtStackObject* ret) noexcept
{
    auto it = g_bridge.find(method);
    if (it == g_bridge.end())
        RETURN_NOT_IMPLEMENTED_ERROR();
    const BridgeEntry* e = it->second;
    int64_t a[8] = {0, 0, 0, 0, 0, 0, 0, 0};
    const interp::RtStackObject* p = params;
    for (uint16_t i = 0; i < e->argc; i++)
    {
        a[i] = load_arg(e->arg_type[i], p);
        p += e->arg_slots[i];
    }
    int64_t r = reinterpret_cast<Fn8>(e->fn)(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
    switch (e->ret_type)
    {
    case RtArgOrLocOrFieldReduceType::Void:
        break;
    case RtArgOrLocOrFieldReduceType::I1:
        interp::EvalStackOp::set_return<int32_t>(ret, static_cast<int8_t>(r));
        break;
    case RtArgOrLocOrFieldReduceType::U1:
        interp::EvalStackOp::set_return<int32_t>(ret, static_cast<uint8_t>(r));
        break;
    case RtArgOrLocOrFieldReduceType::I2:
        interp::EvalStackOp::set_return<int32_t>(ret, static_cast<int16_t>(r));
        break;
    case RtArgOrLocOrFieldReduceType::U2:
        interp::EvalStackOp::set_return<int32_t>(ret, static_cast<uint16_t>(r));
        break;
    case RtArgOrLocOrFieldReduceType::I4:
        interp::EvalStackOp::set_return<int64_t>(ret, static_cast<int32_t>(r));
        break;
    default:
        interp::EvalStackOp::set_return<int64_t>(ret, r);
        break;
    }
    RET_VOID_OK();
}

const PInvokeRegistry* native_bridge_lookup(const metadata::RtMethodInfo* method)
{
    auto it = g_bridge.find(method);
    if (it != g_bridge.end())
        return &it->second->reg;
    if (method->parameter_count > 8 || method->parent == nullptr || method->parent->image == nullptr)
        return nullptr;
    BridgeEntry tmp{};
    tmp.argc = method->parameter_count;
    for (uint16_t i = 0; i < method->parameter_count; i++)
    {
        auto at = interp::InterpDefs::get_reduce_type_and_size_by_typesig(method->parameters[i]);
        if (at.is_err() || !supported(at.unwrap().reduce_type))
            return nullptr;
        tmp.arg_type[i] = at.unwrap().reduce_type;
        tmp.arg_slots[i] = static_cast<uint16_t>(interp::InterpDefs::get_stack_object_size_by_byte_size(at.unwrap().byte_size));
    }
    auto rt = interp::InterpDefs::get_reduce_type_and_size_by_typesig(method->return_type);
    if (rt.is_err() || !(rt.unwrap().reduce_type == RtArgOrLocOrFieldReduceType::Void || supported(rt.unwrap().reduce_type)))
        return nullptr;
    metadata::RtModuleDef* mod = method->parent->image;
    auto im = mod->read_method_impl_map(method->token);
    if (im.is_err() || !im.unwrap().has_value())
        return nullptr;
    metadata::RowImplMap row = im.unwrap().value();
    auto entry_name = mod->get_string(row.import_name);
    auto mref = mod->get_cli_image().read_module_ref(row.import_scope);
    if (entry_name.is_err() || !mref)
        return nullptr;
    auto lib_name = mod->get_string(mref.value().name);
    if (lib_name.is_err())
        return nullptr;
    if (std::strncmp(lib_name.unwrap(), "libSystem.", 10) != 0)
        return nullptr;
    void* h = open_lib(lib_name.unwrap());
    if (!h)
        return nullptr;
    void* fn = dlsym(h, entry_name.unwrap());
    if (!fn)
        return nullptr;
    if (std::getenv("LEANCLR_BRIDGE_TRACE"))
        std::fprintf(stderr, "[bridge] %s::%s -> %s!%s\n", method->parent->name, method->name, lib_name.unwrap(), entry_name.unwrap());
    tmp.ret_type = rt.unwrap().reduce_type;
    tmp.reg = PInvokeRegistry{reinterpret_cast<PInvokeFunction>(fn), bridge_invoker};
    tmp.fn = fn;
    auto* e = new BridgeEntry(tmp);
    g_bridge[method] = e;
    return &e->reg;
}
#else
const PInvokeRegistry* native_bridge_lookup(const metadata::RtMethodInfo*)
{
    return nullptr;
}
#endif
} // namespace vm
} // namespace leanclr
