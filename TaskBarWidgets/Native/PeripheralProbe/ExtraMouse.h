#pragma once
#include "Protocol.h"
namespace peripheral::extra_mouse
{
bool KnownIdentity(unsigned vid, unsigned pid);
bool Admit(Candidate& candidate);
std::optional<Reading> Read(const Candidate& candidate, const std::function<void()>& progress);
void OfflineSelfTests();
}
