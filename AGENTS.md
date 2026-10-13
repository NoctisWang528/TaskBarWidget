# Repository Guidelines

## Project Structure & Scope

TaskBarWidget is one Git repository containing three independent Windows applications. Keep their source trees, dependencies, licenses, and build outputs separate; no combined application or build pipeline exists yet.

- `TrafficMonitor/`: C++20/MFC traffic monitor; solutions, plugin demo, assets, and documentation stay inside this directory.
- `TaskbarLyrics/`: .NET 8 lyrics application, platform-neutral Core, WPF/WebView2 host, xUnit projects, and Vitest tests.
- `AF-Media-Bar/`: .NET 10 WPF media bar; application and solution under `src/`, tests under `tests/`, and engineering guidance under `docs/`.

Read the relevant child `AGENTS.md` before editing a project. Child guides govern that project's implementation and verification; current explicit user instructions take precedence. Work confined to one project should not modify another.

## Build, Test, and Development Commands

Start each group at the workspace root; commands enter the project directory. Windows is required for desktop builds and acceptance checks.

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

The authorized personal remote is `NoctisWang528/TaskBarWidget`. The previous local-only policy is superseded. This migration is authorized for the initial commit and push; future commits, pushes, publication, and upstream contributions require applicable user authorization. Nested `.github/workflows/` files do not run as root workflows automatically.
