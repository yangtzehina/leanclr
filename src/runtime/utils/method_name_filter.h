#pragma once

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <vector>

namespace leanclr
{
namespace utils
{

// Cheap pre-check for the name-keyed registries (internal calls, intrinsics): their keys are
// "<declaring type>::<method name>" followed by nothing, "<...>" (generic arity) or "(...)" (parameters), where the
// declaring type is written as "[namespace.|outer/]<class name>[<type arguments>]". A method can only match a key if its
// class's simple name is one of the class candidates collected here and (class name, method name) one of the pairs,
// which costs a string hash or two, while the full lookup formats the whole signature (several times) for every method
// of every class a program initialises. The class check is remembered for the last class asked about, since a class's
// methods are resolved together. Conservative: every "::" of a key and every '<' after it (up to the parameter list)
// yield a method name candidate; the class name is what follows the last '.' / '/' / '+' of the type part cut at any
// '<' (compiler-generated names such as "<>c" contain '<'). A class whose own name contains '.', '/' or '+' (not
// emitted by C#) always goes to the exact lookup, so a method that some key could match is never rejected (hash
// collisions only let more methods through). Keys must outlive the filter (they are the registries' own keys); add_key
// only collects, the sorted tables are built on the first lookup after it.
class MethodNameFilter
{
  public:
    void add_key(const char* key)
    {
        for (const char* sep = std::strstr(key, "::"); sep != nullptr; sep = std::strstr(sep + 1, "::"))
        {
            const size_t type_len = static_cast<size_t>(sep - key);
            const char* rest = sep + 2;
            // method names never contain '(' but may contain '<' ("<Main>$", explicit implementations of generic
            // interfaces): the name ends before the parameter list, before some '<', or at the end of the key
            size_t n = 0;
            for (; rest[n] != 0 && rest[n] != '('; n++)
            {
                if (rest[n] == '<')
                    add_pair_candidates(key, type_len, hash(rest, n));
            }
            add_pair_candidates(key, type_len, hash(rest, n));
        }
        _sorted = false;
    }

    bool may_match(const char* class_name, const char* method_name)
    {
        if (!_sorted)
            sort_tables();
        if (class_name != _last_class)
        {
            _last_class = class_name;
            _last_class_hash = hash(class_name);
            _last_class_unusual = std::strpbrk(class_name, "./+") != nullptr;
            _last_class_known = _last_class_unusual || std::binary_search(_classes.begin(), _classes.end(), _last_class_hash);
        }
        return _last_class_known &&
               (_last_class_unusual || std::binary_search(_pairs.begin(), _pairs.end(), mix(_last_class_hash, hash(method_name))));
    }

  private:
    static uint64_t hash(const char* s, size_t n)
    {
        uint64_t h = 14695981039346656037ull;
        for (size_t i = 0; i < n; i++)
            h = (h ^ static_cast<uint8_t>(s[i])) * 1099511628211ull;
        return h;
    }

    static uint64_t hash(const char* s)
    {
        uint64_t h = 14695981039346656037ull;
        for (; *s != 0; s++)
            h = (h ^ static_cast<uint8_t>(*s)) * 1099511628211ull;
        return h;
    }

    static uint64_t mix(uint64_t class_hash, uint64_t method_hash)
    {
        return (class_hash * 0x9E3779B97F4A7C15ull) ^ method_hash;
    }

    void add_pair(uint64_t class_hash, uint64_t method_hash)
    {
        _classes.push_back(class_hash);
        _pairs.push_back(mix(class_hash, method_hash));
    }

    // the class name follows the last separator of the type part cut before its type arguments (or at the end)
    void add_pair_candidates(const char* type, size_t type_len, uint64_t method_hash)
    {
        size_t start = 0;
        for (size_t cut = 0; cut <= type_len; cut++)
        {
            if (cut == type_len || type[cut] == '<')
                add_pair(hash(type + start, cut - start), method_hash);
            if (cut < type_len && (type[cut] == '.' || type[cut] == '/' || type[cut] == '+'))
                start = cut + 1;
        }
    }

    static void sort_unique(std::vector<uint64_t>& v)
    {
        std::sort(v.begin(), v.end());
        v.erase(std::unique(v.begin(), v.end()), v.end());
    }

    void sort_tables()
    {
        sort_unique(_classes);
        sort_unique(_pairs);
        _sorted = true;
        _last_class = nullptr;
    }

    std::vector<uint64_t> _classes;
    std::vector<uint64_t> _pairs;
    bool _sorted = true;
    const char* _last_class = nullptr;
    uint64_t _last_class_hash = 0;
    bool _last_class_known = false;
    bool _last_class_unusual = false;
};

} // namespace utils
} // namespace leanclr
