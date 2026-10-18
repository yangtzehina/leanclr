#include "garbage_collector.h"
#include "alloc/general_allocation.h"
#include "metadata/rt_metadata.h"
#include "vm/rt_managed_types.h"

namespace leanclr
{
namespace gc
{

bool GarbageCollector::s_collect_requested = false;

} // namespace gc
} // namespace leanclr
