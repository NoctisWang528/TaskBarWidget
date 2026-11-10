#pragma once
#include "Protocol.h"

namespace peripheral::keyboard_advanced
{
bool KnownIdentity(unsigned vid, unsigned pid);
bool Admit(Candidate& candidate);
// Omni requires the centrally revalidated same-container report-1 sibling in
// candidate.transportPath; absent/ambiguous binding returns no reading.
std::optional<Reading> Read(const Candidate& candidate, const std::function<void()>& progress);
// Pure packet/descriptor fixtures only. Never opens or enumerates a device.
void OfflineSelfTests();
}
