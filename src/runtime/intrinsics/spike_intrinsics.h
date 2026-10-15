#pragma once
#include "vm/intrinsics.h"
namespace leanclr
{
namespace intrinsics
{
// SPIKE: CoreLib [Intrinsic] placeholders (self-recursive IL) the fork did not implement
class SpikeIntrinsics
{
  public:
    static utils::Span<vm::IntrinsicEntry> get_intrinsic_entries() noexcept;
};
} // namespace intrinsics
} // namespace leanclr
