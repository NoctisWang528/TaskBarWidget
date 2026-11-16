#pragma once
#include "Protocol.h"

namespace peripheral::keyboard_advanced
{
bool KnownIdentity(unsigned vid, unsigned pid);
bool Admit(Candidate& candidate);
// Omni requires the centrally revalidated same-container report-1 sibling in
// candidate.transportPath; absent/ambiguous binding returns no reading.
std::optional<Reading> Read(const Candidate& candidate, const std::function<void()>& progress);
// Display metadata from the paired model proven by matching Omni identity replies,
// including a confirmed pair whose battery request receives an explicit NAK.
// Receiver USB product strings/PIDs alone never name a paired keyboard.
std::string ConfirmedModelName(const Candidate& candidate, const Reading& reading);
// Pure packet/descriptor fixtures only. Never opens or enumerates a device.
void OfflineSelfTests();
}
