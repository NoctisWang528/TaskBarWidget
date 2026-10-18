#pragma once
#include <string>

struct StartupRegistrationState
{
    bool taskExists{}, taskEnabled{}, taskValid{}, registryExists{};
    std::wstring taskPath, workingDirectory, legacyPath, currentPath, currentDirectory;
    long error{};
    bool Enabled() const { return taskExists && taskEnabled && taskValid; }
    bool NeedsRepair() const { return registryExists || (taskExists && !Enabled()); }
};

StartupRegistrationState get_startup_registration_for_this_user();
bool is_auto_start_task_active_for_this_user(std::wstring* path);
bool create_auto_start_task_for_this_user(bool runElevated);
bool delete_auto_start_task_for_this_user();
bool delete_legacy_auto_start_for_this_user();
