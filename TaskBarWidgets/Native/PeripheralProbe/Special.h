#pragma once
#include "Protocol.h"
#include <functional>
namespace peripheral
{
// Inventory functions read local PnP/paired metadata only, never battery commands.
std::vector<Candidate> DiscoverSpecial(const std::set<std::string>& excluded = {}, const std::function<void(const std::string&)>& progress = {});
void AttachSerialTransport(Candidate& candidate);
bool IsSpecial(Protocol protocol);
std::optional<Reading> ReadSpecial(const Candidate& candidate, const std::function<void()>& progress = {});
}
