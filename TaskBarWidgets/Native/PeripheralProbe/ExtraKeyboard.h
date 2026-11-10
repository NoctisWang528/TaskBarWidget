#pragma once
#include "Protocol.h"
namespace peripheral::extra_keyboard
{
// Admission is finite and descriptor-gated; Read revalidates split M HUB peers before writes.
bool KnownIdentity(unsigned vid, unsigned pid);
bool Admit(Candidate& candidate);
std::optional<Reading> Read(const Candidate& candidate, const std::function<void()>& progress);
void OfflineSelfTests(); // Pure fixtures only: never opens, enumerates or reads hardware.
}
