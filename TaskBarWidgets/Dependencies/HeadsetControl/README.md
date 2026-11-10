# HeadsetControl 4.1.0

Upstream: [Sapd/HeadsetControl](https://github.com/Sapd/HeadsetControl).
Author: Sapd (Denis Arnst) and the upstream contributors.
License: GPL-3.0-or-later; the unmodified upstream license is included as `LICENSE`.
This is an optional external program, disabled by default. It is not linked into TaskBarWidgets.

Pinned inputs downloaded from the official upstream on 2026-11-10:

| Packaged file | Official source | SHA-256 |
| --- | --- | --- |
| `headsetcontrol.exe` | [4.1.0 Windows x86-64 portable executable](https://github.com/Sapd/HeadsetControl/releases/download/4.1.0/headsetcontrol-windows-x86_64.exe) | `047f0ae26a06d86f6ec5a543166df20ccd47ee07148c915221aa08b97bc190dd` |
| `HeadsetControl-4.1.0-source.zip` | [Official 4.1.0 source tag archive](https://codeload.github.com/Sapd/HeadsetControl/zip/refs/tags/4.1.0) | `4123e7ca7c032b42c2ab8f07fc4859a4d65bac942bc4556b624c3b1329a17aaa` |

The upstream executable signature was downloaded separately as `headsetcontrol.exe.asc`
for maintainer verification. GPG signature verification has not been performed here.
The recorded hashes pin the downloaded bytes; they are not a claim of independent signature verification.
The executable has not been run during this integration. The source ZIP is unmodified and
contains upstream attribution, build instructions and build scripts. No source from `master`
or a later release is substituted for 4.1.0.

## Integration

The provider accepts the product installation root and uses only
`Dependencies/HeadsetControl/headsetcontrol.exe` below it. It verifies the pinned executable
hash before every query. No PATH lookup, installation, download or version-check process runs
at product startup or discovery. Call `Configure(settings)` before discovery and on settings changes.

Queries use exactly `--device VVVV:PPPP --battery --output standard`, in a hidden process,
with bounded output, an eight-second timeout, cancellation and awaited process/output cleanup.
In this fixed version, JSON/YAML/ENV enable extra INFO requests; they must not be used for
battery-only collection. No control flags or follow/poll mode are passed.

The upstream CLI can select only VID/PID and internally deduplicates devices by that pair.
Consequently the provider queries only a uniquely mapped physical Windows container for
an audited subset of supported headset IDs, and rejects missing containers or duplicate pairs.
Topology is checked again before attributing the result. USB topology can change during
an upstream call; a transient attach/detach that Windows never reports cannot be distinguished.
The CLI cannot target a particular HID path or serial number. Devices sharing a pair are
intentionally unavailable, even if one is the only selected device.

Containers with Windows battery properties or a BLE endpoint are left to the Windows provider.
Audeze Maxwell devices are excluded because the 4.1.0 battery initialization sends a setting
packet. Earbuds are excluded because this CLI does not preserve independent left/right/case
roles. The supported subset is encoded in `HeadsetControlDevices`; upstream support alone
does not establish compatibility or hardware validation for this integration.

Known coarse SteelSeries levels remain level text, calibrated Logitech readings are marked
estimated, unavailable batteries are never zero, and charging is true only when explicitly
reported (otherwise unknown). Failed/disconnected reads retain the previous successful timestamp.
HeadsetControl source-level protocol review and fixture tests do not establish actual hardware acceptance.

Distributions must retain this README, LICENSE, the matching source ZIP and upstream notices.
Product packaging is responsible for copying these inputs to the same installation-relative path.
