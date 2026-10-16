#include "aot_module.h"
#include "utils/hashmap.h"
#include "utils/string_util.h"

#include <cstdio>
#include <cstring>

namespace leanclr
{
namespace metadata
{

utils::HashMap<const char*, const RtAotModuleData*, utils::CStrHasher, utils::CStrCompare> s_aotModuleMap;

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

void AotModule::note_attached(const RtAotModuleData* aot_data)
{
    s_attachedModules++;
    s_attachedMethods += aot_data->method_def_entry_count;
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
    if (method->generic_method != nullptr || method->generic_container != nullptr || method->parent->generic_container != nullptr)
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
