#include "aot_module.h"
#include <cstdint>
#include <cstdlib>
#include "utils/hashmap.h"
#include "utils/string_util.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <vector>

namespace leanclr
{
namespace metadata
{

utils::HashMap<const char*, const RtAotModuleData*, utils::CStrHasher, utils::CStrCompare> s_aotModuleMap;
// key -> instantiation, filled from each module's generic_method_entries when its AOT code is attached
static utils::HashMap<const char*, const RtAotGenericMethodData*, utils::CStrHasher, utils::CStrCompare> s_genericMethodMap;
static uint32_t s_attachedGenericMethods = 0;
// The generic method definitions ("<assembly>!<rid>" key prefixes) that have compiled instantiations, by assembly name, and
// the same resolved per module id: a method whose definition has none is rejected without formatting its key (most of the
// generic methods a program inflates, e.g. every method of each generic class instance). Collected cheaply at attach
// (no allocation per entry; the rid lists are sorted on first use) and resolved per module on the first lookup after an
// attach, since a later module can bring instantiations of an earlier module's methods.
struct GenericDefRids
{
    const char* assembly; // points into a key: not 0-terminated
    size_t assembly_len;
    std::vector<uint32_t> rids;
    bool sorted;
};
static std::vector<GenericDefRids> s_genericDefRids;
static std::vector<GenericDefRids*> s_genericDefRidsByModule;
static std::vector<uint8_t> s_genericDefRidsResolved;
// get_invoker and get_method_pointer look up the same method back to back
static const RtMethodInfo* s_lastGenericMethod = nullptr;
static const RtAotMethodDefData* s_lastGenericResult = nullptr;

// Diagnostics (bisecting a wrong instantiation): LEANCLR_AOT_GENERIC_RANGE=lo:hi uses only the compiled instantiations whose
// registration index (attach order, then table order) is in [lo, hi); LEANCLR_AOT_GENERIC_ONLY=<key> only that one;
// LEANCLR_AOT_GENERIC_TRACE=1 prints each one handed out.
struct GenericDiag
{
    bool inited = false;
    bool trace = false;
    const char* only = nullptr;
    uint32_t lo = 0;
    uint32_t hi = UINT32_MAX;
};
static GenericDiag s_genericDiag;
static utils::HashMap<const RtAotGenericMethodData*, uint32_t> s_genericIndex;

static const GenericDiag& generic_diag()
{
    if (!s_genericDiag.inited)
    {
        s_genericDiag.inited = true;
        const char* trace = std::getenv("LEANCLR_AOT_GENERIC_TRACE");
        s_genericDiag.trace = trace != nullptr && trace[0] == '1';
        s_genericDiag.only = std::getenv("LEANCLR_AOT_GENERIC_ONLY");
        if (const char* range = std::getenv("LEANCLR_AOT_GENERIC_RANGE"))
        {
            unsigned long lo = std::strtoul(range, nullptr, 10);
            const char* colon = std::strchr(range, ':');
            s_genericDiag.lo = static_cast<uint32_t>(lo);
            s_genericDiag.hi = colon ? static_cast<uint32_t>(std::strtoul(colon + 1, nullptr, 10)) : UINT32_MAX;
        }
    }
    return s_genericDiag;
}

static AotBindingPolicy s_bindingPolicy = AotBindingPolicy::Unchecked;
static uint32_t s_attachedModules = 0;
static uint32_t s_attachedMethods = 0;
static uint32_t s_rejectedModules = 0;
static char s_lastRejection[320] = {0};

static const uint8_t* get_module_mvid(const RtModuleDef* module)
{
    auto row = module->get_cli_image().read_module(1);
    if (!row || row->mvid == 0)
    {
        return nullptr;
    }
    const auto& heap = module->get_cli_image().get_guid_heap();
    uint64_t offset = static_cast<uint64_t>(row->mvid - 1) * 16u;
    if (offset + 16u > heap.size)
    {
        return nullptr;
    }
    return heap.data + offset;
}

static void format_mvid(char* out, const uint8_t* mvid)
{
    static const char hex[] = "0123456789abcdef";
    for (int i = 0; i < 8; i++)
    {
        out[i * 2] = hex[mvid[i] >> 4];
        out[i * 2 + 1] = hex[mvid[i] & 15];
    }
    out[16] = 0;
}

void AotModule::set_binding_policy(AotBindingPolicy policy)
{
    s_bindingPolicy = policy;
}

AotBindingPolicy AotModule::get_binding_policy()
{
    return s_bindingPolicy;
}

uint32_t AotModule::get_attached_module_count()
{
    return s_attachedModules;
}

uint32_t AotModule::get_attached_method_count()
{
    return s_attachedMethods;
}

uint32_t AotModule::get_rejected_module_count()
{
    return s_rejectedModules;
}

const char* AotModule::get_last_rejection()
{
    return s_lastRejection;
}

// "<assembly>!<hex rid><...>" -> s_genericDefRids
static void note_generic_def(const char* key)
{
    size_t len = 0;
    while (key[len] != '!' && key[len] != 0)
        len++;
    if (key[len] != '!')
        return;
    uint32_t rid = 0;
    for (const char* p = key + len + 1;; p++)
    {
        char c = *p;
        if (c >= '0' && c <= '9')
            rid = (rid << 4) | static_cast<uint32_t>(c - '0');
        else if (c >= 'a' && c <= 'f')
            rid = (rid << 4) | static_cast<uint32_t>(c - 'a' + 10);
        else
            break;
    }
    // entries come grouped by assembly: try the previous entry's group first
    static size_t last = SIZE_MAX;
    auto same = [key, len](const GenericDefRids& g) { return g.assembly_len == len && std::memcmp(g.assembly, key, len) == 0; };
    if (last >= s_genericDefRids.size() || !same(s_genericDefRids[last]))
    {
        last = SIZE_MAX;
        for (size_t i = 0; i < s_genericDefRids.size() && last == SIZE_MAX; i++)
        {
            if (same(s_genericDefRids[i]))
                last = i;
        }
        if (last == SIZE_MAX)
        {
            s_genericDefRids.push_back(GenericDefRids{key, len, {}, true});
            last = s_genericDefRids.size() - 1;
        }
    }
    s_genericDefRids[last].rids.push_back(rid);
    s_genericDefRids[last].sorted = false;
}

void AotModule::note_attached(const RtAotModuleData* aot_data)
{
    s_attachedModules++;
    s_attachedMethods += aot_data->method_def_entry_count;
    const GenericDiag& diag = generic_diag();
    const bool diag_active = diag.trace || diag.only != nullptr || diag.lo != 0 || diag.hi != UINT32_MAX;
    s_genericMethodMap.reserve(s_genericMethodMap.size() + aot_data->generic_method_entry_count);
    for (uint32_t i = 0; i < aot_data->generic_method_entry_count; i++)
    {
        const RtAotGenericMethodData* entry = aot_data->generic_method_entries + i;
        if (s_genericMethodMap.insert({entry->key, entry}).second)
        {
            note_generic_def(entry->key);
            if (diag_active)
            {
                s_genericIndex.insert({entry, s_attachedGenericMethods});
            }
            s_attachedGenericMethods++;
        }
    }
    s_genericDefRidsByModule.clear();
    s_genericDefRidsResolved.clear();
    s_lastGenericMethod = nullptr;
}

uint32_t AotModule::get_attached_generic_method_count()
{
    return s_attachedGenericMethods;
}

namespace
{
struct KeyWriter
{
    char* buf;
    size_t size;
    size_t len;
    bool ok;

    void put(char c)
    {
        if (len + 1 < size)
            buf[len++] = c;
        else
            ok = false;
    }
    void put(const char* s)
    {
        while (*s)
            put(*s++);
    }
    void hex(uint32_t v, int min_digits)
    {
        static const char digits[] = "0123456789abcdef";
        char tmp[8];
        int n = 0;
        do
        {
            tmp[n++] = digits[v & 15];
            v >>= 4;
        } while (v != 0 && n < 8);
        while (n < min_digits)
            tmp[n++] = '0';
        while (n > 0)
            put(tmp[--n]);
    }
    void dec(uint32_t v)
    {
        char tmp[10];
        int n = 0;
        do
        {
            tmp[n++] = static_cast<char>('0' + v % 10);
            v /= 10;
        } while (v != 0);
        while (n > 0)
            put(tmp[--n]);
    }
    // "<assembly>:<rid>" of a TypeDef / MethodDef gid
    bool def(uint32_t gid, char sep)
    {
        RtModuleDef* mod = RtModuleDef::get_module_by_id(RtMetadata::decode_module_id_from_gid(gid));
        if (mod == nullptr)
            return false;
        put(mod->get_name_no_ext());
        put(sep);
        hex(RtMetadata::decode_rid_from_gid(gid), 1);
        return true;
    }
};

bool append_type_key(KeyWriter& w, const RtTypeSig* sig, int depth);

bool append_inst_key(KeyWriter& w, const RtGenericInst* inst, int depth)
{
    if (inst == nullptr)
        return true;
    for (uint8_t i = 0; i < inst->generic_arg_count; i++)
    {
        if (i > 0)
            w.put(',');
        if (!append_type_key(w, inst->generic_args[i], depth + 1))
            return false;
    }
    return true;
}

bool append_type_key(KeyWriter& w, const RtTypeSig* sig, int depth)
{
    if (sig == nullptr || depth > 32)
        return false;
    if (sig->by_ref)
        w.put('R');
    switch (sig->ele_type)
    {
    case RtElementType::Void:
    case RtElementType::Boolean:
    case RtElementType::Char:
    case RtElementType::I1:
    case RtElementType::U1:
    case RtElementType::I2:
    case RtElementType::U2:
    case RtElementType::I4:
    case RtElementType::U4:
    case RtElementType::I8:
    case RtElementType::U8:
    case RtElementType::R4:
    case RtElementType::R8:
    case RtElementType::String:
    case RtElementType::TypedByRef:
    case RtElementType::I:
    case RtElementType::U:
    case RtElementType::Object:
        w.put('E');
        w.hex(static_cast<uint32_t>(sig->ele_type), 2);
        return true;
    case RtElementType::Class:
    case RtElementType::ValueType:
        w.put('T');
        return w.def(sig->data.type_def_gid, ':');
    case RtElementType::GenericInst:
    {
        const RtGenericClass* gc = sig->data.generic_class;
        w.put('G');
        w.put('T');
        if (!w.def(gc->base_type_def_gid, ':'))
            return false;
        w.put('<');
        if (!append_inst_key(w, gc->class_inst, depth))
            return false;
        w.put('>');
        return true;
    }
    case RtElementType::SZArray:
        w.put('Z');
        return append_type_key(w, sig->data.element_type, depth + 1);
    case RtElementType::Array:
        w.put('A');
        w.dec(sig->data.array_type->rank);
        return append_type_key(w, sig->data.array_type->ele_type, depth + 1);
    case RtElementType::Ptr:
        w.put('P');
        return append_type_key(w, sig->data.element_type, depth + 1);
    default:
        return false; // generic parameters, function pointers
    }
}
} // namespace

// whether any compiled instantiation has this generic method definition
static bool has_generic_method_def(uint32_t method_def_gid)
{
    uint32_t module_id = RtMetadata::decode_module_id_from_gid(method_def_gid);
    if (module_id >= s_genericDefRidsResolved.size())
    {
        s_genericDefRidsResolved.resize(module_id + 1, 0);
        s_genericDefRidsByModule.resize(module_id + 1, nullptr);
    }
    if (!s_genericDefRidsResolved[module_id])
    {
        RtModuleDef* mod = RtModuleDef::get_module_by_id(module_id);
        if (mod == nullptr)
            return false;
        const char* name = mod->get_name_no_ext();
        size_t len = std::strlen(name);
        GenericDefRids* found = nullptr;
        for (auto& g : s_genericDefRids)
        {
            if (g.assembly_len == len && std::memcmp(g.assembly, name, len) == 0)
                found = &g;
        }
        if (found != nullptr && !found->sorted)
        {
            std::sort(found->rids.begin(), found->rids.end());
            found->rids.erase(std::unique(found->rids.begin(), found->rids.end()), found->rids.end());
            found->sorted = true;
        }
        s_genericDefRidsByModule[module_id] = found;
        s_genericDefRidsResolved[module_id] = 1;
    }
    const GenericDefRids* group = s_genericDefRidsByModule[module_id];
    return group != nullptr && std::binary_search(group->rids.begin(), group->rids.end(), RtMetadata::decode_rid_from_gid(method_def_gid));
}

bool AotModule::format_generic_method_key(const RtMethodInfo* method, char* buf, size_t size)
{
    const RtGenericMethod* gm = method->generic_method;
    if (gm == nullptr || method->generic_container != nullptr || size == 0)
        return false;
    KeyWriter w{buf, size, 0, true};
    if (!w.def(gm->base_method_gid, '!'))
        return false;
    w.put('<');
    if (!append_inst_key(w, gm->generic_context.class_inst, 0))
        return false;
    w.put(';');
    if (!append_inst_key(w, gm->generic_context.method_inst, 0))
        return false;
    w.put('>');
    buf[w.len] = 0;
    return w.ok;
}

RtResult<bool> AotModule::check_bindings(const RtModuleDef* mod, const RtAotModuleData* aot_data)
{
    if (s_bindingPolicy == AotBindingPolicy::Unchecked)
    {
        RET_OK(true);
    }
    const char* problem = nullptr;
    const char* dep_name = "";
    char have[17] = "-";
    char want[17] = "-";
    if (aot_data->binding_count == 0 || aot_data->bindings == nullptr)
    {
        problem = "no binding data in the generated code";
    }
    for (uint32_t i = 0; problem == nullptr && i < aot_data->binding_count; i++)
    {
        const RtAotAssemblyBinding& b = aot_data->bindings[i];
        dep_name = b.assembly_name;
        // the module itself is not registered yet while its AOT data is being checked
        const RtModuleDef* dep = std::strcmp(b.assembly_name, mod->get_name_no_ext()) == 0 ? mod : RtModuleDef::find_module(b.assembly_name);
        if (dep == nullptr)
        {
            problem = "not loaded";
            break;
        }
        const uint8_t* mvid = get_module_mvid(dep);
        format_mvid(want, b.mvid);
        if (mvid == nullptr)
        {
            problem = "has no MVID";
            break;
        }
        if (std::memcmp(mvid, b.mvid, 16) != 0)
        {
            format_mvid(have, mvid);
            problem = "MVID differs";
            break;
        }
    }
    if (problem == nullptr)
    {
        RET_OK(true);
    }
    s_rejectedModules++;
    std::snprintf(s_lastRejection, sizeof(s_lastRejection), "AOT code of %s not used: %s %s (loaded %s, generated against %s)",
                  mod->get_name_no_ext(), dep_name, problem, have, want);
    std::fprintf(stderr, "[leanclr] %s%s\n", s_lastRejection, s_bindingPolicy == AotBindingPolicy::Strict ? "" : "; running it interpreted");
    if (s_bindingPolicy == AotBindingPolicy::Strict)
    {
        RET_ERR(RtErr::BadImageFormat);
    }
    RET_OK(false);
}

void AotModule::register_aot_modules(const RtAotModulesData* initData)
{
    for (uint32_t i = 0; i < initData->module_count; i++)
    {
        const RtAotModuleData* moduleData = initData->modules[i];
        s_aotModuleMap.insert({moduleData->module_name, moduleData});
    }
}

const RtAotModuleData* AotModule::find_aot_module_by_name(const char* module_name)
{
    auto it = s_aotModuleMap.find(module_name);
    return it != s_aotModuleMap.end() ? it->second : nullptr;
}

const RtAotMethodDefData* AotModule::find_aot_method_impl(const RtMethodInfo* method)
{
    if (method->generic_method != nullptr)
    {
        if (s_genericMethodMap.size() == 0 || !has_generic_method_def(method->generic_method->base_method_gid))
        {
            return nullptr;
        }
        if (method == s_lastGenericMethod)
        {
            return s_lastGenericResult;
        }
        char key[512];
        if (!format_generic_method_key(method, key, sizeof(key)))
        {
            return nullptr;
        }
        s_lastGenericMethod = method;
        s_lastGenericResult = nullptr;
        auto it = s_genericMethodMap.find(key);
        if (it == s_genericMethodMap.end())
        {
            return nullptr;
        }
        const RtAotGenericMethodData* entry = it->second;
        const GenericDiag& diag = generic_diag();
        if (diag.trace || diag.only != nullptr || diag.lo != 0 || diag.hi != UINT32_MAX)
        {
            uint32_t index = s_genericIndex.find(entry)->second;
            if (index < diag.lo || index >= diag.hi || (diag.only != nullptr && std::strcmp(diag.only, key) != 0))
            {
                return nullptr;
            }
            if (diag.trace)
            {
                std::fprintf(stderr, "aot-generic %u %s\n", index, key);
            }
        }
        if (*entry->self_slot == nullptr)
        {
            *entry->self_slot = method;
        }
        s_lastGenericResult = &entry->data;
        return &entry->data;
    }
    if (method->generic_container != nullptr || method->parent->generic_container != nullptr)
    {
        return nullptr;
    }
    const RtAotMethodDefData* aotMethodDefData = find_aot_method_def_impl(method->parent->image, method->token);
    if (aotMethodDefData == nullptr)
    {
        return nullptr;
    }
    return aotMethodDefData;
}

const RtAotMethodDefData* AotModule::find_aot_method_def_impl(const RtModuleDef* module, EncodedTokenId token)
{
    const RtAotModuleData* aotModuleData = module->get_aot_module_data();
    if (aotModuleData == nullptr || aotModuleData->method_def_entry_count == 0)
    {
        return nullptr;
    }
    const RtAotMethodDefData* entries = aotModuleData->method_def_entries;
    uint32_t count = aotModuleData->method_def_entry_count;
    uint32_t left = 0;
    uint32_t right = count;
    while (left < right)
    {
        uint32_t mid = left + (right - left) / 2;
        EncodedTokenId cur_token = entries[mid].token;
        if (cur_token < token)
        {
            left = mid + 1;
        }
        else if (cur_token > token)
        {
            right = mid;
        }
        else
        {
            return &entries[mid];
        }
    }
    return nullptr;
}

const RtAotMethodMonoPInvokeCallbackData* AotModule::find_mono_pinvoke_callback_method(const RtModuleDef* module, EncodedTokenId token)
{
    const RtAotModuleData* aotModuleData = module->get_aot_module_data();
    if (aotModuleData == nullptr || aotModuleData->mono_pinvoke_callback_entry_count == 0)
    {
        return nullptr;
    }
    const RtAotMethodMonoPInvokeCallbackData* entries = aotModuleData->mono_pinvoke_callback_entries;
    uint32_t count = aotModuleData->mono_pinvoke_callback_entry_count;
    uint32_t left = 0;
    uint32_t right = count;
    while (left < right)
    {
        uint32_t mid = left + (right - left) / 2;
        EncodedTokenId cur_token = entries[mid].token;
        if (cur_token < token)
        {
            left = mid + 1;
        }
        else if (cur_token > token)
        {
            right = mid;
        }
        else
        {
            return &entries[mid];
        }
    }
    return nullptr;
}

RtResult<const RtMethodInfo*> AotModule::find_mono_pinvoke_callback_method_by_native_ptr(RtNativeMethodPointer ptr)
{
    if (ptr == nullptr)
    {
        RET_OK(nullptr);
    }
    utils::Vector<RtModuleDef*> modules;
    RtModuleDef::get_registered_modules(modules);
    for (RtModuleDef* module : modules)
    {
        if (module == nullptr)
        {
            continue;
        }
        const RtAotModuleData* aotModuleData = module->get_aot_module_data();
        if (aotModuleData == nullptr || aotModuleData->mono_pinvoke_callback_entry_count == 0)
        {
            continue;
        }
        const RtAotMethodMonoPInvokeCallbackData* entries = aotModuleData->mono_pinvoke_callback_entries;
        uint32_t count = aotModuleData->mono_pinvoke_callback_entry_count;
        for (uint32_t i = 0; i < count; ++i)
        {
            if (entries[i].native_method_ptr == ptr)
            {
                return module->get_method_by_token(RtToken::decode(entries[i].token), RtGenericContainerContext{}, nullptr);
            }
        }
    }
    RET_OK(nullptr);
}

} // namespace metadata
} // namespace leanclr
