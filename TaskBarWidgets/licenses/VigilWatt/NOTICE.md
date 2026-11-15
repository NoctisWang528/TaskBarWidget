# VigilWatt protocol attribution

Source: https://github.com/evren-oct/vigil-watt
Pinned revision: `4df830043296760f5d5c273f4cdc0ea9456c4bc7`
Copyright (c) 2026 evren. Original MIT license: LICENSE in this directory.
Original contributor acknowledgments: CONTRIBUTORS.md in this directory.

TaskBarWidgets selectively compiles reviewed HID protocol code in a separate .NET 8 process. The original snapshot and SHA-256 provenance, adaptation sources/patches, and developer update script are preserved in the product source tree under Dependencies/VigilWatt, PeripheralHost and scripts/sync-vigil-watt.ps1.

Local adaptations: host-owned lifecycle and demand, memory-only configuration, endpoint-preserving identities, stricter response checks and transaction correlation, Basilisk V4 Pro descriptors, target model names, and diagnostic metadata. The upstream application UI, history database, startup registration and updater are not included. Native fallback protocols retain their separate notices.

Basilisk V4 protocol facts were cross-checked with publicly reported descriptors and test results, not copied from the GPL OpenRazer implementation:
https://github.com/openrazer/openrazer/issues/2958
https://github.com/openrazer/openrazer/pull/2975

Protocol implementation and automated replay tests do not establish physical hardware validation. New Viper V4 Pro and Basilisk V4 Pro integrations await TaskBarWidgets hardware acceptance.
