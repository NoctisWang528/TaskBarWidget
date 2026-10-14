# Repository Guidelines

## Project Structure & Scope

TaskBarWidget is one Git repository. Active product development belongs exclusively to `TaskBarWidgets/`. The three original applications below are read-only references: do not edit, restore, build, or generate files in them during integration work. Necessary source, assets, tests, and license notices may be copied into the new product. Its builds must succeed without any original project directory present.

- `TaskBarWidgets/`: the standalone integrated Windows 11 product; read its `AGENTS.md` before implementation. All new application code, dependencies, scripts, tests, and build outputs belong here.
- `docs/`: workspace decisions and the integration requirements.

- `TrafficMonitor/`: C++20/MFC traffic monitor; solutions, plugin demo, assets, and documentation stay inside this directory.
- `TaskbarLyrics/`: .NET 8 lyrics application, platform-neutral Core, WPF/WebView2 host, xUnit projects, and Vitest tests.
- `AF-Media-Bar/`: .NET 10 WPF media bar; application and solution under `src/`, tests under `tests/`, and engineering guidance under `docs/`.

Reference-project guides describe their original code. Do not copy those guides as governing instructions for the new application. The user's 2026-10-13 integration authorization permits local implementation, copying, builds, and tests; Git publication requires a separate user request; it does not authorize replacing the daily installation at `C:\Users\nocti\Documents\TrafficMonitor`.

## Build, Test, and Development Commands

Use `TaskBarWidgets/scripts/build.ps1` for integration builds. Windows is required. Commands below are historical reference-project instructions, not integration build steps. Never invoke them as dependencies of the new product.

```powershell
# TrafficMonitor: VS 2022 Developer PowerShell, v143, MFC, Windows SDK
Set-Location TrafficMonitor
msbuild TrafficMonitor_Lite.sln /m /p:Configuration="Debug (lite)" /p:Platform=x64
& '.\Bin\x64\Debug (lite)\TrafficMonitor.exe'
```

```powershell
# TaskbarLyrics: .NET 8 SDK; npm ci installs Web test dependencies
Set-Location TaskbarLyrics
dotnet build TaskbarLyrics.sln
dotnet run --project TaskbarLyrics.App
npm ci
npm run test:web
```

```powershell
# AF-Media-Bar: SDK selected by global.json; build/test serially
Set-Location AF-Media-Bar
dotnet restore src/AFMediaBar.slnx -r win-x64
dotnet build src/AFMediaBar.slnx -c Release --no-restore -p:BuildInParallel=false
dotnet test tests/AFMediaBar.Layout.Tests -c Release --no-build --no-restore
```

## Coding Style & Testing

Preserve each project's `.editorconfig`, encoding, naming, architecture, and localization conventions. TrafficMonitor uses four-space indentation, separate-line braces, and MFC naming. Avoid cross-project reformatting and speculative shared abstractions.

TrafficMonitor runs assertion helpers at Debug startup. TaskbarLyrics uses xUnit and Vitest; follow its `scripts/verify.ps1` guidance. AF-Media-Bar has dedicated tests and Windows acceptance requirements. Run checks appropriate to changed behavior; record unexecuted checks. Taskbar, DPI, Explorer, audio, and multi-monitor behavior require real Windows validation.

## Git, Commits & Review

The root `.git` is the only repository; do not initialize child repositories or submodules. Use focused messages such as `docs(workspace): update contributor guidance`. Describe affected projects, validation, and remaining risks; include screenshots for UI changes. Keep build output, credentials, runtime data, and dependency caches untracked. Preserve upstream attribution and licenses.

The authorized personal remote is `NoctisWang528/TaskBarWidget`. On 2026-10-14 the user explicitly authorized this documentation/repository cleanup, commit and push. This is not standing authorization for later pushes, releases, or upstream contributions. Nested `.github/workflows/` files do not run as root workflows automatically.
