#pragma once
#include "pinvoke.h"
namespace leanclr
{
namespace vm
{
// SPIKE: resolve a blittable P/Invoke to a symbol of a real native library (non-Windows native hosts only).
const PInvokeRegistry* native_bridge_lookup(const metadata::RtMethodInfo* method);
} // namespace vm
} // namespace leanclr
