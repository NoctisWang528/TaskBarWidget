# Repository Guidelines

This guide applies to the `TrafficMonitor/` project inside TaskBarWidget. Run the commands below from this directory; Git operations use the parent workspace repository.

## Project Structure & Module Organization

- `TrafficMonitor/`: Windows MFC application, dialogs, monitoring logic, and utilities. `PdhHardwareQuery/` contains performance-counter monitoring; `res/`, `language/`, and `skins/` hold application assets.
- `include/PluginInterface.h`: shared plugin API; `PluginDemo/`: example DLL plugin.
- `OpenHardwareMonitorApi/`: hardware-monitoring wrapper used by the standard solution.
- `TrafficMonitor_Lite.sln`: Lite application and plugin demo; `TrafficMonitor.sln`: also includes the hardware wrapper.
- `Screenshots/`, `images/`, `Help*.md`, and `UpdateLog/`: documentation and supporting images. Debug test helpers live in `TrafficMonitor/Test.cpp`.

## Build, Test, and Development Commands

Use a Visual Studio 2022 Developer PowerShell with Desktop development with C++, v143 tools, MFC, and a Windows SDK installed. Run commands from the repository root:

```powershell
msbuild TrafficMonitor_Lite.sln /m /p:Configuration="Debug (lite)" /p:Platform=x64
msbuild TrafficMonitor_Lite.sln /m /p:Configuration="Release (lite)" /p:Platform=x64
& '.\Bin\x64\Debug (lite)\TrafficMonitor.exe'
```

These build Debug, build Release, and launch the Debug application. CI uses Release Lite with `x64`, `x86`, and `ARM64EC`; x86 output omits the platform directory. Use Visual Studio F5 for debugging. Standard builds use `TrafficMonitor.sln` with `Debug` or `Release`.

## Coding Style & Naming Conventions

Follow surrounding C++20/MFC code: four-space indentation, braces on separate lines, `C`-prefixed classes such as `CTaskBarDlg`, PascalCase methods, and `m_` member variables. Keep `stdafx.h` first in application implementation files. Preserve existing file encodings and localized text. No repository-wide formatter or linter configuration is present; avoid unrelated reformatting. Register new source files in the relevant `.vcxproj` and `.vcxproj.filters` files.

## Testing Guidelines

There is no standalone test framework or coverage threshold. Debug startup calls `CTest::Test()`, which currently runs assertion-based plugin-version checks. Add focused `Test<Feature>()` helpers there when appropriate and invoke them explicitly. Build and launch Debug to exercise assertions. Manually verify affected floating-window, taskbar, adapter-selection, settings, skin, or plugin behavior; record Windows version, architecture, and reproduction steps.

## Commit & Pull Request Guidelines

Use focused messages such as `fix(trafficmonitor): correct adapter selection`. For review, describe the problem, behavior change, build configuration, and validation; reference related issues when available and include screenshots for UI changes. Exclude generated `Bin/`, `Obj/`, `.vs/`, and personal configuration files.

## Workspace Repository Policy

This project is tracked by the parent TaskBarWidget repository and its personal GitHub remote. Do not create a nested `.git` directory. Follow the parent `AGENTS.md` for commit, push, and publication authorization.
