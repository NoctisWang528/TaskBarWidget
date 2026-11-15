# Isolated VigilWatt protocol source

Upstream: https://github.com/evren-oct/vigil-watt, pinned commit
`4df830043296760f5d5c273f4cdc0ea9456c4bc7` (MIT; original notices remain in `upstream/`).
The 307 selected original files were compared against that commit's raw ZIP and match
byte-for-byte, including BOM and newline bytes. They are never edited for integration.

`source-manifest.json` inventories SHA-256 hashes for the original files, adapted
copies, and real unified patches in `patches/`. The host compiles only HID, Models,
Plugins and necessary common code, substituting `Adapted/` copies for originals.
It excludes DeviceManager, UI, BLE watchers, SQLite, persistent settings and log sinks.
`admitted-plugins.json` and `COMPATIBILITY.md` describe the actual explicit registry,
not the full imported source tree. Protocol admission is not hardware acceptance.

Run from the repository root:

```powershell
TaskBarWidgets/scripts/sync-vigil-watt.ps1 -Check
TaskBarWidgets/scripts/sync-vigil-watt.ps1 -Check -Ref <40-character-commit-SHA>
TaskBarWidgets/scripts/sync-vigil-watt.ps1 -Ref <40-character-commit-SHA>
```

`-Check` is offline and verifies every baseline/adapted/patch hash and exact patch
replay. `-Check -Ref` additionally downloads the fixed repository's commit ZIP,
prints added/changed paths and counts, and stages the selected source plus every
adaptation without replacing the baseline. `-Ref` verifies the current baseline
first, then performs the same staging and replacement with rollback on rename failure.
Missing selected files or any patch context/count conflict fail the whole import;
there is no fuzzy application or silently discarded adaptation. Upstream scripts
are never executed. Concurrent edits are not supported: finish the current review
and stop builders before importing another revision.

Changing the upstream commit is only a source import. Review the registry and
adaptation behavior, regenerate compatibility/provenance, update the diagnostics
revision and integration documentation, and refresh packaged license/contributor
copies and NOTICE. The sync tool prints the concrete required follow-up paths;
it deliberately does not edit Media, product documentation or packaged notices.
Run host replay/lifecycle tests and the complete product/package checks before delivery.

The host wire contract is version 1: correlated `discover` and `read` operations,
raw endpoint/unit identities in `deviceIds`, and `excludedPaths`/`excludedIdentities`
quarantine. Native-only extra request fields are ignored. A correlated `progress`
event precedes an endpoint probe so the parent can quarantine a blocked endpoint.
Each device also returns `queryId`, its private plugin query key. A restart discovery
with nonempty `deviceIds` requires the corresponding `queryIds` array; it probes
only those endpoint paths and child slots and returns only the requested identities.
Missing query keys fail closed. An explicit `discover` with empty `deviceIds` retains
the complete inventory behavior. These keys never replace public target identity.
Diagnostics reasons are fixed machine codes. Output is UTF-8 JSONL, at most 1 MiB
per line. No plugin, listener, watcher or device handle is retained after a response.
`--parent-pid <pid>` is required for ordinary operation; stdin EOF and a wait on a
SYNCHRONIZE-only parent handle terminate the helper even during blocked driver I/O.
`--self-test` is the explicit synthetic entry point and never queries hardware.

Production output belongs under `Media/PeripheralHost/` beside the shared-runtime
files; executable name is `TaskBarWidgets.PeripheralHost.exe`. Publishing is
framework-dependent win-x64 for net8.0-windows, with no runtime trimming or WinRT
SDK projections. Protocol I/O uses Win32 P/Invoke and the system-shared .NET 8 Core runtime.

Razer Basilisk V4 PID/channel evidence:
https://github.com/openrazer/openrazer/issues/2958. Only public descriptor facts
were used for E2/E3 admission; no GPL implementation was copied. This does not
establish successful battery reads on those devices. V3 SE keeps its separate codec.
BlackShark 0555 is intentionally unclaimed here and remains on the product's audited
native `blackshark_pa` fallback. Logitech HID++ targets require validated 0x0003
unit+model evidence and checks before/after battery reads; unsupported unit identity
remains an unreadable endpoint-local candidate, never a stable physical target.

Receiver containers and USB receiver serials are not target identity evidence.
CM unique USB bus IDs are exposed only for documented direct device-body PIDs;
DualSense's admitted direct Bluetooth channel may identify its target container.
Haute chip identity is explicitly scoped to the adapter, not a paired controller.
Unknown identity remains independent, even when names or model numbers match.
