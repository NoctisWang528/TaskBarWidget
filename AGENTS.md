# Repository Guidelines

## Scope

The root `.git` is the only repository. Product code, dependencies, assets, scripts, tests and build output belong to `TaskBarWidgets/`; read its `AGENTS.md` before implementation. Root `docs/` contains workspace decisions and requirements.

`TrafficMonitor/`, `TaskbarLyrics/` and `AF-Media-Bar/` may exist as ignored local references. They are not tracked or published. Never edit, restore, build or generate files in those folders. Copy reusable source/assets with their notices into the product when needed. Product builds must succeed when all three reference directories are absent.

## Development

Use `TaskBarWidgets/scripts/build.ps1` for Windows integration builds. Preserve source encodings and existing C++/C# conventions; avoid broad reformatting. Run relevant behavior checks and record desktop-only limitations. Tests and offscreen rendering do not substitute for real audio, Explorer, mixed-DPI or hardware acceptance.

Never replace the user's daily installation, change startup registration, or start the normal product without applicable authorization. Keep user configuration and history out of Git and Release packages. Preserve attribution and all upstream licensing requirements.

## Git and releases

The authorized remote is `NoctisWang528/TaskBarWidget`. On 2026-10-16 the user authorized updating documentation, untracking the three original reference trees while keeping local copies, committing/pushing accumulated product changes, generating a product icon, and publishing the first ZIP Release. The user subsequently authorized committing/pushing the weather configuration fix and publishing its new ZIP Release using the build-time version. These requests are not standing authorization for later pushes/releases or changes to repository visibility.

Release versions use build time `yymmdd-hhmm` in Asia/Taipei. Build clean distributable packages without developer settings, logs, caches or secrets. Retain third-party licenses. Do not initialize child repositories or rewrite published history as part of routine cleanup.

On 2026-10-18 the user authorized documenting and committing/pushing the two accumulated UI/interaction/settings changes and publishing their clean ZIP as release 261018-1213. Personal build packages and their data are excluded. This does not authorize future releases or visibility changes.
