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
- WinUI3 owns all visible settings and cards. Four independent equal-width ordinary cards: weather, traffic, devices, media by default top to bottom; weather is opt-out, and all four can be enabled, disabled, and reordered from the “Quick Cards” settings page. An optional narrow update card occupies a separate second column only when an update is ready. Heatmap is twelve weeks of upload+download, square cells, subtle glow, no scrolling. Device selection uses flyouts without resizing or dismissing the group. Settings gear belongs on the media card.
- Module pool and one/two composition lanes define taskbar contents. Click opens the right property drawer; remove retains labels/colors. Stable IDs and explicit rows persist. Shared font/background/opacity live in one group; secondary checkboxes are compact.
- Explicit two-row layouts share column widths (maximum of the upper/lower item requirements) and column spacing. Preserve lane order; exclusive double-line items span their own column. Preview and native geometry must use the same pairing rule. Heatmap colors are relative to the visible twelve-week daily peak; unknown, zero and future dates remain distinct.
- The media/audio panel and default communication endpoint synchronization are always enabled; the audio preference page and global hotkey feature are removed. Device synchronization occurs only on an explicit switch. Lyrics behavior, appearance, matching, per-track offsets and diagnostics share one navigation category. Source selection shows per-application icons and spacious menu rows.
- Cards dismiss on an actual outside click, Escape, entry toggle or opening settings; foreground/focus changes alone must never dismiss them. A scoped mouse listener must never swallow global input and must be removed on hide/shutdown. The native entry HWND is verified against the native host PID before forwarding.
- Audio mutations return readback directly to the cards; mute never writes volume. Serialize audio commands separately from slow settings/history requests, bound queues, and do not let older snapshots overwrite newer operations.
- Preserve user configuration/history; about links to https://github.com/NoctisWang528/TaskBarWidget. Respect system theme, high contrast and reduced motion.
- Factory defaults are compiled into the executable and the media assembly as resources (`Native/TrafficMonitor/defaults/*.default.ini`, `Media/Defaults/media-settings.default.json`). They are seeded byte-for-byte only when the target user file is absent, are never merged into or written over an existing configuration, never appear as package files, and must not contain credentials, adapter names, sensor names, cities, fonts, paths, version stamps or window geometry. The main config is UTF-8 with BOM and CRLF; the Battery and PowerMon INIs must stay pure ASCII because their modules rewrite them through ANSI profile APIs.
- Quick-card weather symbols are two-tone layered vector geometry drawn in `UI/Cards/WeatherIconArt.cs` with separate light and dark palettes and no packaged artwork. The hero temperature shrinks to the measured column width and always keeps a gap from the icon; it must never be clipped by or painted under the symbol.
- Product updates use only the fixed repository's stable Releases and the protocol in docs/更新协议.md. Automatic check/download defaults on and saves independently; installation requires explicit confirmation through the shared App path, with ordinary/weather drafts and active saves guarded. A ready update may add a 220-DIP card in a second column to the right of the ordinary cards, bottom-aligned; never auto-open cards or notify. The update card stays outside ordinary-card IDs, ordering, enablement and resource mode, and participates in geometry/theme/lifecycle operations.
- The standalone native updater replaces only inventoried program files in the same installation after validated shutdown approval and normal process exit. Back up program and personal data, require native/UI health, retain unfinished journals for recovery, and never force-close user processes. Do not auto-install shared runtimes or change startup registration. Older installations without the updater require one manual upgrade.

## Architecture and code

`Native/` owns the single taskbar window, lifecycle, monitoring and DirectComposition/WebView2 rendering. `Media/` owns headless .NET lyric/SMTC/audio/settings services, retaining WPF dispatcher and an unshown settings compatibility adapter. `UI/` owns WinUI3 windows. Do not show old WPF or HTML settings. Children exit with their parent; only the native root is a user entry point.

Pipes are current-user-only, versioned, bounded and correlated. Keep UI threads responsive; validate typed writes, preserve newer edits during async save/readback, cancel stale operations, and release resources on shutdown. See [IPC.md](IPC.md).

Use existing proven services before adding abstractions. Preserve C++/C# conventions, native text encodings and source licenses. Native resource scripts may be UTF-16; Chinese UTF-8 C++ headers require BOM or the appropriate compiler encoding flag. Avoid unrelated formatting.

Built-in providers/data managers must initialize after MFC through function-local singletons, never pre-entry-point globals. Native UI WinRT initializes once as STA in InitInstance; balance only successful initialization. Built-in resources belong to the EXE.

Detach D2D targets before resizing swap chains even when caches retain the context. Mode transitions must not change the taskbar HWND's layering or hit-testing. WinUI card dimensions have one AppWindow geometry owner; content animations must not move HWND bounds. Elevated in-window module dragging uses pointer capture, not system OLE/DataPackage drag-drop.

## Builds and validation

Use `scripts/build.ps1` for complete hardware Release builds, not Lite-only validation. Run independent .NET builds serially to avoid generated-output races. Default output is `artifacts/builds/yyyyMMdd-HHmmss/`; refuse default-directory collisions. Release versions use the build time as `yymmdd-hhmm`; do not hardcode a past candidate version. Successful packages include build-info.json; only complete successful builds update artifacts/latest-build.txt.

Publish Media/ and UI/ beside the main executable. Media/UI use system-installed x64 .NET 8 Desktop/Core with LatestPatch roll-forward, and shared Windows App Runtime 2.5.1 or compatible later 2.x. Do not bundle Runtime/ or WinUI native framework DLLs. Keep the small bootstrapper, managed projections and VC143 CRT app-local. Native preflight must validate hostfxr framework resolution and actual WinUI bootstrap before normal startup, showing actionable download links without auto-installation. Never modify global environment variables. Product PRI/XBF remain explicit publish inputs, present and hash-identical to generated resources. Validate the actual published product probe rather than a separate resource host.

Keep targeted scenario regressions, not wholesale upstream suites or markup trivia. Never use test counts as desktop acceptance. For provider initialization changes run Native/tests/run-builtin-startup-test.ps1 after a fresh native build. UI component checks use scripts/validate-winui.ps1; zero-sized hidden content is not a visual pass. For UI startup/resource changes also run scripts/test-ui-templates.ps1 against the final package: actual templates load offscreen without activation or native/media host startup. A project-reference validation host or no-arguments bootstrap can mask missing resources.

For runtime/package changes run scripts/test-shared-runtime.ps1 and Native/tests/run-managed-runtime-test.ps1. They use isolated diagnostic entry points, never the normal product, and assert system-shared Core/Desktop/WinUI paths. Cover missing .NET, wrong major version, missing bootstrapper and unavailable WinUI version without uninstalling or changing real system runtimes. Package checks verify shared framework declarations and absence of bundled frameworks. Do not enable unsafe trimming; preserve functionality. Fonts are system-provided.

For updater changes run the targeted Media update tests and scripts/test-updater.ps1 in isolated fixtures; inspect final package update.json, ZIP/SHA256SUMS.txt, and app-files.json consistency. Published diagnostics must exercise the optional update card, in-place progress and revision ordering. Simulated helper restart/rollback does not establish real Explorer, elevation, player/audio, mixed-DPI, or personal-data acceptance. Publish authorized releases as draft first; upload all update assets, download/verify them, and only then publish the stable Release. No new publication authority is implied by these rules.

Distribute only Simplified Chinese and English language files, managed satellite assemblies and native MUI resources. Preserve neutral/framework PRI and globalization data. Keep all required third-party notices; licensing files are not unused runtime dependencies.

The standing user instruction is not to auto-start the normal product. Do not run smoke-native.ps1/smoke-media.ps1 without desktop-launch authorization. Report actual player, audio, Explorer, multi-monitor, DPI and visual acceptance as pending where untested.

## Git, user data and cleanup

Keep builds, dependencies, caches, crash dumps, logs and personal runtime data ignored; source assets, language INIs, pinned binary dependencies and license notices are intentional inputs. Public instructions must not contain a contributor's personal absolute paths. Publish a clean package without user configuration, weather credentials, history, local backups or probe logs/images.

The product and new integration code inherit the root Anti 996 license. Preserve original authorship and the separate MIT/MPL/OFL and other third-party terms in THIRD-PARTY-NOTICES.md and licenses/. Include these notices in release packages; do not describe the whole product as GPL or relicense third-party code.

Routine retirement covers the immediate previous build; the 2026-10-18 cleanup additionally authorizes historical local artifacts. Verify absolute paths and personal-data backups first; refuse live processes, locked drivers, links, unsafe paths or data conflicts. Never unload a driver or kill user applications to force cleanup. NVIDIA/other overlays are external; do not change global gaming settings or undocumented driver profiles.

## Delegation

Subagents are permitted, all using gpt-6.1-sol. Assign disjoint ownership, coordinate protocol contracts and build slots, and integrate/review results before reporting completion.

On 2026-10-18 the user accepted lightweight build 261018-1408 and authorized documenting, committing/pushing all accumulated slimming/shared-runtime changes, publishing its clean ZIP, and cleaning historical local builds and temporary files after verified personal-data backups. Preserve the running installation, current delivery and build, source dependencies and backups. This does not authorize later releases, remote release deletion or visibility changes.

On 2026-10-18 the user authorized fixing settings/card theme synchronization and readability, producing personal build and clean release packages, and committing/pushing this change. No normal-product launch, installation replacement or GitHub Release publication is authorized by this request.

On 2026-10-18 the user authorized committing and pushing the application updater implementation and documentation, rebuilding a clean Release with a fresh build-time version, and publishing it after draft asset verification so the existing 261018-1638 build can test updating. Preserve personal packages and daily installations; no normal-product launch or startup changes. This is not standing authorization for subsequent releases.

On 2026-10-18 the user additionally authorized committing/pushing the updater process-path comparison fix and rebuilding/publishing a fresh complete Release containing the corrected native updater, so no second updater replacement is needed after upgrade. Use Claude Code for a bounded independent review. Do not start or modify the daily installation.
