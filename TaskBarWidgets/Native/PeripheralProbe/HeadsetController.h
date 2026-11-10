#pragma once
#include "Protocol.h"

namespace peripheral::headset_controller
{
bool KnownIdentity(unsigned vid, unsigned pid);
bool Admit(Candidate& candidate);
// Caller revalidates the selected collection first. initialize() journals an
// attempted once-only session request BEFORE it is sent; false means listen only.
std::optional<Reading> Read(const Candidate& candidate, const std::function<void()>& progress,
    const std::function<bool()>& initialize);
void OfflineSelfTests();
}
