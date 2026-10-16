#include "profile.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "metadata/metadata_name.h"
#include "metadata/aot_module.h"
#include "interp/machine_state.h"
#include <map>
#include "metadata/module_def.h"
#include "interp/interp_defs.h"
#include "utils/string_builder.h"

namespace leanclr
{
namespace profile
{

#if LEANCLR_PGO_PROFILE
namespace
{
struct ExportCandidate
{
    const char* assembly;
    std::string signature;
    uint64_t calls;
    uint64_t cost;
    bool aot;
    const char* why; // interpreted methods: why no AOT code ran ("" for AOT)
    std::string key; // closed generic instantiations: AotModule::format_generic_method_key ("" otherwise)
    std::string callers; // JSON array of the top callers by calls (profiled frames), "" when unknown
};



// AOT coverage: did the method run as AOT code, and if not, the first reason that applies.
bool is_aot_method(const metadata::RtMethodInfo* method)
{
    return method->invoker_type == metadata::RtInvokerType::Aot || method->invoker_type == metadata::RtInvokerType::AotVirtualAdjustThunk;
}

const char* interpreted_reason(const metadata::RtMethodInfo* method)
{
    if (is_aot_method(method))
    {
        return "";
    }
    if (method->generic_method != nullptr || method->generic_container != nullptr || method->parent->generic_container != nullptr ||
        (method->parent->by_val != nullptr && method->parent->by_val->ele_type == metadata::RtElementType::GenericInst))
    {
        return "generic";
    }
    if (method->parent->image == nullptr || method->parent->image->get_aot_module_data() == nullptr)
    {
        return "module";
    }
    // compiled modules handle try / catch / finally / fault; a filter clause (or LeanAOT --leanaot-no-exception-handlers)
    // keeps the method interpreted
    if (method->interp_data != nullptr && method->interp_data->exception_clause_count > 0)
    {
        return "eh";
    }
    return "other";
}

bool g_enabled = true;
std::vector<ProfileSlot> g_slots;
std::vector<const metadata::RtMethodInfo*> g_slot_methods;
// (caller, callee) -> calls while enabled; the caller is the frame below the callee's on the machine state's frame stack
// (interpreted and AOT frames alike; nullptr = called from native code)
std::map<std::pair<const metadata::RtMethodInfo*, const metadata::RtMethodInfo*>, uint64_t> g_edges;

inline size_t to_slot_index(uint32_t profile_slot)
{
    return static_cast<size_t>(profile_slot - 1);
}

inline const char* get_assembly_name(const metadata::RtMethodInfo* method)
{
    if (method == nullptr || method->parent == nullptr || method->parent->image == nullptr)
    {
        return "Unknown";
    }
    const char* name = method->parent->image->get_name_no_ext();
    return (name && name[0] != '\0') ? name : "Unknown";
}

RtResult<std::string> build_method_signature(const metadata::RtMethodInfo* method)
{
    utils::Utf8StringBuilder sb;
    RET_ERR_ON_FAIL(metadata::MetadataName::append_type_full_name(sb, method->return_type, metadata::TypeNameFormat::DnlibToString, false));
    sb.append_char(' ');
    RET_ERR_ON_FAIL(metadata::MetadataName::append_method_full_name_with_params(sb, method, metadata::TypeNameFormat::DnlibToString));
    RET_OK(std::string(sb.get_const_chars()));
}

void append_json_escaped(std::string& out, const char* text)
{
    if (text == nullptr)
    {
        return;
    }
    for (; *text != '\0'; ++text)
    {
        const char c = *text;
        switch (c)
        {
        case '"':
            out.append("\\\"");
            break;
        case '\\':
            out.append("\\\\");
            break;
        case '\b':
            out.append("\\b");
            break;
        case '\f':
            out.append("\\f");
            break;
        case '\n':
            out.append("\\n");
            break;
        case '\r':
            out.append("\\r");
            break;
        case '\t':
            out.append("\\t");
            break;
        default:
            out.push_back(c);
            break;
        }
    }
}

void append_json_escaped(std::string& out, const std::string& text)
{
    append_json_escaped(out, text.c_str());
}

std::string build_callers_json(const metadata::RtMethodInfo* callee)
{
    std::vector<std::pair<uint64_t, const metadata::RtMethodInfo*>> list;
    for (auto it = g_edges.begin(); it != g_edges.end(); ++it)
    {
        if (it->first.second == callee)
            list.push_back({it->second, it->first.first});
    }
    if (list.empty())
        return std::string();
    std::sort(list.begin(), list.end(),
              [](const std::pair<uint64_t, const metadata::RtMethodInfo*>& a, const std::pair<uint64_t, const metadata::RtMethodInfo*>& b)
              { return a.first > b.first; });
    if (list.size() > 8)
        list.resize(8);
    std::string json = "[";
    for (size_t i = 0; i < list.size(); ++i)
    {
        if (i > 0)
            json.push_back(',');
        json.append("{\"calls\":");
        json.append(std::to_string(list[i].first));
        json.append(",\"signature\":\"");
        std::string sig = "(native)";
        if (list[i].second != nullptr)
        {
            auto r = build_method_signature(list[i].second);
            sig = r.is_ok() ? r.unwrap() : std::string("?");
        }
        for (char c : sig)
        {
            if (c == '"' || c == '\\')
                json.push_back('\\');
            json.push_back(c);
        }
        json.append("\"}");
    }
    json.push_back(']');
    return json;
}

template <typename CallsGetter, typename CostGetter>
std::vector<ExportCandidate> collect_candidates(const ExportOptions& options, CallsGetter calls_getter, CostGetter cost_getter)
{
    std::vector<ExportCandidate> candidates;
    candidates.reserve(g_slots.size());
    for (size_t i = 0; i < g_slots.size(); ++i)
    {
        const ProfileSlot& slot = g_slots[i];
        uint64_t calls = calls_getter(slot);
        uint64_t cost = cost_getter(slot);
        if (calls < options.min_calls || cost < options.min_cost)
        {
            continue;
        }
        const metadata::RtMethodInfo* method = g_slot_methods[i];
        auto signature_result = build_method_signature(method);
        if (signature_result.is_err())
        {
            continue;
        }
        char key[512];
        candidates.push_back(ExportCandidate{
            get_assembly_name(method),
            signature_result.unwrap(),
            calls,
            cost,
            is_aot_method(method),
            interpreted_reason(method),
            metadata::AotModule::format_generic_method_key(method, key, sizeof(key)) ? std::string(key) : std::string(),
            build_callers_json(method),
        });
    }

    std::sort(candidates.begin(), candidates.end(),
              [](const ExportCandidate& a, const ExportCandidate& b)
              {
                  if (a.cost != b.cost)
                  {
                      return a.cost > b.cost;
                  }
                  if (a.calls != b.calls)
                  {
                      return a.calls > b.calls;
                  }
                  const int assembly_cmp = std::strcmp(a.assembly, b.assembly);
                  if (assembly_cmp != 0)
                  {
                      return assembly_cmp < 0;
                  }
                  return a.signature < b.signature;
              });

    if (options.top_n > 0 && candidates.size() > options.top_n)
    {
        candidates.resize(options.top_n);
    }
    return candidates;
}

std::string build_candidates_json(const std::vector<ExportCandidate>& candidates)
{
    if (candidates.empty())
    {
        return "[]";
    }

    std::string json;
    json.append("[\n");
    for (size_t i = 0; i < candidates.size(); ++i)
    {
        if (i > 0)
        {
            json.append(",\n");
        }
        const ExportCandidate& candidate = candidates[i];
        json.append("{\"assembly\":\"");
        append_json_escaped(json, candidate.assembly);
        json.append("\",\"signature\":\"");
        append_json_escaped(json, candidate.signature);
        json.append("\",\"calls\":");
        json.append(std::to_string(candidate.calls));
        json.append(",\"cost\":");
        json.append(std::to_string(candidate.cost));
        json.append(",\"aot\":");
        json.append(candidate.aot ? "true" : "false");
        if (!candidate.aot)
        {
            json.append(",\"why\":\"");
            json.append(candidate.why);
            json.push_back('"');
        }
        if (!candidate.key.empty())
        {
            json.append(",\"key\":\"");
            append_json_escaped(json, candidate.key);
            json.push_back('"');
        }
        if (!candidate.callers.empty())
        {
            json.append(",\"callers\":");
            json.append(candidate.callers);
        }
        json.push_back('}');
    }
    json.append("\n]");
    return json;
}

RtResultVoid write_json_to_file(const char* path, const std::string& json)
{
    FILE* fp = std::fopen(path, "wb");
    if (fp == nullptr)
    {
        RET_ERR(RtErr::FileNotFound);
    }
    std::fwrite(json.data(), 1, json.size(), fp);
    std::fclose(fp);
    RET_VOID_OK();
}
} // namespace
#endif

bool Profile::is_enabled() noexcept
{
#if LEANCLR_PGO_PROFILE
    return g_enabled;
#else
    return false;
#endif
}

void Profile::set_enabled(bool enabled) noexcept
{
#if LEANCLR_PGO_PROFILE
    g_enabled = enabled;
#else
    (void)enabled;
#endif
}

uint32_t Profile::ensure_slot(metadata::RtMethodInfo* method) noexcept
{
#if LEANCLR_PGO_PROFILE
    if (method == nullptr)
    {
        return 0;
    }

    if (method->profile_slot != 0)
    {
        return static_cast<uint32_t>(to_slot_index(method->profile_slot));
    }

    const uint32_t new_slot = static_cast<uint32_t>(g_slots.size() + 1);
    g_slots.push_back(ProfileSlot{0, 0, 0, 0});
    g_slot_methods.push_back(method);
    method->profile_slot = new_slot;
    return new_slot - 1;
#else
    (void)method;
    return 0;
#endif
}

void Profile::inc_call_count(const metadata::RtMethodInfo* method, uint64_t delta) noexcept
{
#if LEANCLR_PGO_PROFILE
    if (!g_enabled || method == nullptr)
    {
        return;
    }
    metadata::RtMethodInfo* mutable_method = const_cast<metadata::RtMethodInfo*>(method);
    uint32_t slot_idx = ensure_slot(mutable_method);
    g_slots[slot_idx].period_calls += delta;
    // the callee's own frame is on top; an interpreted caller of AOT code also pushes a frame for the callee before
    // the AOT function pushes its own, so skip one frame of the same method
    auto frames = interp::MachineState::get_global_machine_state().get_active_frames();
    const metadata::RtMethodInfo* caller = nullptr;
    if (frames.size() >= 2)
    {
        size_t i = frames.size() - 2;
        if (frames[i].method == method && i > 0)
            i--;
        caller = frames[i].method;
    }
    g_edges[{caller, method}] += delta;
#else
    (void)method;
    (void)delta;
#endif
}

void Profile::add_cost(const metadata::RtMethodInfo* method, uint64_t cost) noexcept
{
#if LEANCLR_PGO_PROFILE
    if (!g_enabled || method == nullptr || cost == 0)
    {
        return;
    }
    metadata::RtMethodInfo* mutable_method = const_cast<metadata::RtMethodInfo*>(method);
    uint32_t slot_idx = ensure_slot(mutable_method);
    g_slots[slot_idx].period_cost += cost;
#else
    (void)method;
    (void)cost;
#endif
}

void Profile::end_period() noexcept
{
#if LEANCLR_PGO_PROFILE
    for (ProfileSlot& slot : g_slots)
    {
        slot.global_calls += slot.period_calls;
        slot.global_cost += slot.period_cost;
        slot.period_calls = 0;
        slot.period_cost = 0;
    }
#endif
}

void Profile::reset_period() noexcept
{
#if LEANCLR_PGO_PROFILE
    for (ProfileSlot& slot : g_slots)
    {
        slot.period_calls = 0;
        slot.period_cost = 0;
    }
#endif
}

#if LEANCLR_PGO_PROFILE
static uint64_t g_transitions[static_cast<size_t>(Transition::Count)] = {};
#endif

void Profile::inc_transition(Transition t) noexcept
{
#if LEANCLR_PGO_PROFILE
    if (g_enabled)
        g_transitions[static_cast<size_t>(t)]++;
#else
    (void)t;
#endif
}

uint64_t Profile::get_transition_count(Transition t) noexcept
{
#if LEANCLR_PGO_PROFILE
    return g_transitions[static_cast<size_t>(t)];
#else
    (void)t;
    return 0;
#endif
}

void Profile::reset_global() noexcept
{
#if LEANCLR_PGO_PROFILE
    for (uint64_t& n : g_transitions)
        n = 0;
    g_edges.clear();
    for (ProfileSlot& slot : g_slots)
    {
        slot.period_calls = 0;
        slot.period_cost = 0;
        slot.global_calls = 0;
        slot.global_cost = 0;
    }
#endif
}

size_t Profile::get_period_entries(ProfileEntry* out, size_t capacity) noexcept
{
#if LEANCLR_PGO_PROFILE
    size_t count = 0;
    for (size_t i = 0; i < g_slots.size(); ++i)
    {
        const ProfileSlot& slot = g_slots[i];
        if (slot.period_calls == 0 && slot.period_cost == 0)
        {
            continue;
        }
        if (out != nullptr && count < capacity)
        {
            out[count] = ProfileEntry{g_slot_methods[i], slot.period_calls, slot.period_cost};
        }
        ++count;
    }
    return count;
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

size_t Profile::get_global_entries(ProfileEntry* out, size_t capacity) noexcept
{
#if LEANCLR_PGO_PROFILE
    size_t count = 0;
    for (size_t i = 0; i < g_slots.size(); ++i)
    {
        const ProfileSlot& slot = g_slots[i];
        if (slot.global_calls == 0 && slot.global_cost == 0)
        {
            continue;
        }
        if (out != nullptr && count < capacity)
        {
            out[count] = ProfileEntry{g_slot_methods[i], slot.global_calls, slot.global_cost};
        }
        ++count;
    }
    return count;
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

RtResult<std::string> Profile::get_period_stats_json(const ExportOptions& options) noexcept
{
#if LEANCLR_PGO_PROFILE
    const std::vector<ExportCandidate> candidates =
        collect_candidates(options, [](const ProfileSlot& slot) { return slot.period_calls; }, [](const ProfileSlot& slot) { return slot.period_cost; });
    RET_OK(build_candidates_json(candidates));
#else
    (void)options;
    RET_OK(std::string("[]"));
#endif
}

RtResult<std::string> Profile::get_global_stats_json(const ExportOptions& options) noexcept
{
#if LEANCLR_PGO_PROFILE
    const std::vector<ExportCandidate> candidates = collect_candidates(
        options, [](const ProfileSlot& slot) { return slot.global_calls + slot.period_calls; },
        [](const ProfileSlot& slot) { return slot.global_cost + slot.period_cost; });
    RET_OK(build_candidates_json(candidates));
#else
    (void)options;
    RET_OK(std::string("[]"));
#endif
}

RtResultVoid Profile::export_period_stats_json(const char* path, const ExportOptions& options) noexcept
{
#if LEANCLR_PGO_PROFILE
    if (path == nullptr || path[0] == '\0')
    {
        RET_ERR(RtErr::ArgumentNull);
    }
    DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL(std::string, json, get_period_stats_json(options));
    return write_json_to_file(path, json);
#else
    (void)path;
    (void)options;
    RET_VOID_OK();
#endif
}

RtResultVoid Profile::export_global_stats_json(const char* path, const ExportOptions& options) noexcept
{
#if LEANCLR_PGO_PROFILE
    if (path == nullptr || path[0] == '\0')
    {
        RET_ERR(RtErr::ArgumentNull);
    }
    DECLARING_AND_UNWRAP_OR_RET_ERR_ON_FAIL(std::string, json, get_global_stats_json(options));
    return write_json_to_file(path, json);
#else
    (void)path;
    (void)options;
    RET_VOID_OK();
#endif
}

} // namespace profile
} // namespace leanclr
