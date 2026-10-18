# TaskBarWidgets implementation rules

## Scope and authorization

Implement the current [requirements](../docs/需求与架构.md) and [UI specification](docs/界面统一方案.md). All product source, dependencies, scripts, tests and generated output belong under this directory. `../TrafficMonitor`, `../TaskbarLyrics` and `../AF-Media-Bar` are optional local, untracked and ignored read-only references, absent from GitHub and release packages; never restore/build/write there or depend on them during product builds. A fresh product checkout must build without those directories. No child Git repositories.

Local implementation/builds/tests are authorized. On 2026-10-16 the user authorized the documentation/repository cleanup, commit, push and first GitHub Release ZIP, then separately authorized committing/pushing the weather configuration fix and publishing its new ZIP Release. This is not standing authorization for future publication or changes to repository visibility. Never replace or stop the daily TrafficMonitor installation, register startup, or publish elsewhere without applicable authorization.

On 2026-10-18 the user additionally authorized documenting, committing and pushing the two accumulated UI/interaction/settings changes and publishing build 261018-1213 as a clean ZIP Release. This authorization is specific to that release; personal build data must remain local.

## Product behavior

- Keep TrafficMonitor's native taskbar, hardware/network monitoring, configurable display items and layout. No desktop main window, skins, monitoring thresholds/notifications, external plugin loading or plugin page.
- Compile only Battery, PowerMon and WeatherPro as built-in features. Keep historic display IDs and local INI compatibility; expose settings on Hardware. Never return stored credentials or log them. Explicit user-entered weather keys may travel only in a write-only configuration command over the current-user IPC pipe; keep blank-as-unchanged and explicit clearing, and never send keys to arbitrary hosts.
- QQ/NetEase playing shows lyrics with existing word scanning, translation and effects; pause/stop restores monitoring. Browser playback shows metadata/controls and must never query lyrics.
- Wheel temporarily selects monitoring/media until the next effective playback-state or source change. Track/position changes alone do not reset it. Left click opens cards; right click opens the application menu; no taskbar hover details.
- WinUI3 owns all visible settings and cards. Three independent equal-width cards: traffic, devices, media from top to bottom. Heatmap is twelve weeks of upload+download, square cells, subtle glow, no scrolling. Device selection uses flyouts without resizing or dismissing the group. Settings gear belongs on the media card.
- Module pool and one/two composition lanes define taskbar contents. Click opens the right property drawer; remove retains labels/colors. Stable IDs and explicit rows persist. Shared font/background/opacity live in one group; secondary checkboxes are compact.
- Explicit two-row layouts share column widths (maximum of the upper/lower item requirements) and column spacing. Preserve lane order; exclusive double-line items span their own column. Preview and native geometry must use the same pairing rule. Heatmap colors are relative to the visible twelve-week daily peak; unknown, zero and future dates remain distinct.
- The media/audio panel and default communication endpoint synchronization are always enabled; the audio preference page and global hotkey feature are removed. Device synchronization occurs only on an explicit switch. Lyrics behavior, appearance, matching, per-track offsets and diagnostics share one navigation category. Source selection shows per-application icons and spacious menu rows.
- Cards dismiss on an actual outside click, Escape, entry toggle or opening settings; foreground/focus changes alone must never dismiss them. A scoped mouse listener must never swallow global input and must be removed on hide/shutdown. The native entry HWND is verified against the native host PID before forwarding.
- Audio mutations return readback directly to the cards; mute never writes volume. Serialize audio commands separately from slow settings/history requests, bound queues, and do not let older snapshots overwrite newer operations.
- Preserve user configuration/history; about links to https://github.com/NoctisWang528/TaskBarWidget. Respect system theme, high contrast and reduced motion.

## Architecture and code

`Native/` owns the single taskbar window, lifecycle, monitoring and DirectComposition/WebView2 rendering. `Media/` owns headless .NET lyric/SMTC/audio/settings services, retaining WPF dispatcher and an unshown settings compatibility adapter. `UI/` owns WinUI3 windows. Do not show old WPF or HTML settings. Children exit with their parent; only the native root is a user entry point.

Pipes are current-user-only, versioned, bounded and correlated. Keep UI threads responsive; validate typed writes, preserve newer edits during async save/readback, cancel stale operations, and release resources on shutdown. See [IPC.md](IPC.md).

Use existing proven services before adding abstractions. Preserve C++/C# conventions, native text encodings and source licenses. Native resource scripts may be UTF-16; Chinese UTF-8 C++ headers require BOM or the appropriate compiler encoding flag. Avoid unrelated formatting.

Built-in providers/data managers must initialize after MFC through function-local singletons, never pre-entry-point globals. Native UI WinRT initializes once as STA in InitInstance; balance only successful initialization. Built-in resources belong to the EXE.

Detach D2D targets before resizing swap chains even when caches retain the context. Mode transitions must not change the taskbar HWND's layering or hit-testing. WinUI card dimensions have one AppWindow geometry owner; content animations must not move HWND bounds. Elevated in-window module dragging uses pointer capture, not system OLE/DataPackage drag-drop.

## Builds and validation

Use `scripts/build.ps1` for complete hardware Release builds, not Lite-only validation. Run independent .NET builds serially to avoid generated-output races. Default output is `artifacts/builds/yyyyMMdd-HHmmss/`; refuse default-directory collisions. Release versions use the build time as `yymmdd-hhmm`; do not hardcode a past candidate version. Successful packages include build-info.json; only complete successful builds update artifacts/latest-build.txt.

Publish Media/ and UI/ beside the main executable. WinUI is self-contained with app-local VC143 CRT. Its own generated PRI/XBF must be explicit publish inputs, present in the final package and hash-identical to the current generated resources. Do not copy resource files from a validation host to repair packaging.

Keep targeted scenario regressions, not wholesale upstream suites or markup trivia. Never use test counts as desktop acceptance. For provider initialization changes run Native/tests/run-builtin-startup-test.ps1 after a fresh native build. UI component checks use scripts/validate-winui.ps1; zero-sized hidden content is not a visual pass. For UI startup/resource changes also run scripts/test-ui-templates.ps1 against the final package: actual templates load offscreen without activation or native/media host startup. A project-reference validation host or no-arguments bootstrap can mask missing resources.

The standing user instruction is not to auto-start the normal product. Do not run smoke-native.ps1/smoke-media.ps1 without desktop-launch authorization. Report actual player, audio, Explorer, multi-monitor, DPI and visual acceptance as pending where untested.

## Git, user data and cleanup

Keep builds, dependencies, caches, crash dumps, logs and personal runtime data ignored; source assets, language INIs, pinned binary dependencies and license notices are intentional inputs. Public instructions must not contain a contributor's personal absolute paths. Publish a clean package without user configuration, weather credentials, history, local backups or probe logs/images.

The product and new integration code inherit the root Anti 996 license. Preserve original authorship and the separate MIT/MPL/OFL and other third-party terms in THIRD-PARTY-NOTICES.md and licenses/. Include these notices in release packages; do not describe the whole product as GPL or relicense third-party code.

The user permits deleting only the immediate previous build after a successful new package and verified user-data backup/migration. Use the retirement script; refuse live processes, locked drivers, links, unsafe paths or data conflicts. Never unload a driver or kill user applications to force cleanup. NVIDIA/other overlays are external; do not change global gaming settings or undocumented driver profiles.

## Delegation

Subagents are permitted, all using gpt-6.1-sol. Assign disjoint ownership, coordinate protocol contracts and build slots, and integrate/review results before reporting completion.
