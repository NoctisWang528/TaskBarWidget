# KeyboardAdvanced integration and evidence

Owned implementation: `KeyboardAdvanced.h/.cpp`. No catalog, protocol, probe,
ExtraKeyboard, project, or build-script changes are included.

Public contract is `peripheral::keyboard_advanced::{KnownIdentity, Admit, Read,
OfflineSelfTests}`. `Admit` sets `Protocol::KeyboardAdvanced`, `adapter`,
`kind="keyboard"`, and direct-USB `wired` where applicable. Register these four
entry points centrally; `OfflineSelfTests()` uses synthetic packets/descriptors
and cleanup callbacks only, with exceptions on failure and no HID calls.

## Scope

| Adapter | USB admission | Collection/report proof |
| --- | --- | --- |
| keycool_k19 | 3151:5026 | MI2 FFFF:2, unique Feature/native10..65; paired uint32 1326/2382 |
| valkyrie_vk99_ry | 374A:A263/A278/A281 | MI2 FFFF:2, same relay; paired uint32 3116/3165/3907/3997 |
| dareu_a98 | 260D:0037 / 0051 | MI3 000C:1, Feature0/body64; receiver PID0051, direct target0 |
| dareu_flex87 | 260D:0211 | MI1 000C:1, Feature0A/native65, paired PID0210 |
| mchose_k99v3 | 3837:3033 | 0001:0, unnumbered64 I/O, exact K99 V3/G75 V2/G87 V2 2.4G product name; G75/G87 MI1 |
| pmo_wave75 | 36B0:3002 | MI1 FF60:61, unique same-ID I/O/native33 or65; downstream VID/PID allowlist |
| ipi_aurora | 372E:106C | MI2 FF60:61, report9/native20..65, UUID120000000003 |
| titan_t87 | 056D:C077 | MI1 FF87:20 / FF88:21 / FF00:1, report6/native32 |
| rog_omni_keyboard | 0B05:1ACE / 1AD0 | MI2 FF00:1/report2 plus centrally bound FF02:1/report1, native64 |
| lofree | 388D:0025 / wired0024 | FF1C:92, report4/native32 |

Ten adapters, fifteen VID/PID identities. `mchose_qhw` is deliberately excluded
and remains owned by ExtraKeyboard. No sampled firmware revision is hardcoded.
Unknown/shared IDs never establish a paired model without its protocol proof.

## ROG sibling binding required centrally

For ROG only, put the unique report-1 identity collection's **selected, centrally
revalidated** path into the report-2 candidate's `transportPath`. Establish equal
fresh physical/container identity, VID/PID, revision and MI2 for both candidates.
Require FF02:1/report1 I/O64 for the sibling and FF00:1/report2 I/O64 for the main
path. Legacy 1ACE additionally requires `mi_02&col01` / `mi_02&col02` respectively.
The reader opens the supplied paths, checks native identity and descriptor proof
again, retains the sibling's observed report map for its final recheck, and
performs initial/final paired-model checks.
It never enumerates or derives a sibling path. Missing binding returns unknown.

## Transaction limits and published state

Each selected path has an owned HIDAPI handle. Progress precedes each native call;
the parent watchdog remains responsible for a driver call that does not return.
An eight-second shared deadline, finite drain/read loops, and a 512 regular-call
per-handle limit bound responsive devices. Every result requires two fresh effective battery
replies and final protocol identity/online or active-profile checks. PMO/Lofree
start/stop queries have scoped cleanup, including a bounded stop attempt on errors.
No lighting, keymap, profile activation, firmware, pairing, or repair writes exist.

Zero is retained where the source protocol proves it; FLEX and Aurora reject zero
according to their source validity rules. Charging remains unknown for K19,
Valkyrie, A98, PMO, Aurora, Titan, ROG, and Lofree. FLEX and GLW expose their defined
charging field. DAREU and Titan percentages retain their approximate flag.

## Source and validation limits

Protocol facts were read from MouseDock DB179 `devices.json` and the local
`dis_wireless_keycool_k19`, `dareu_a98`, `dareu_flex87`, `mchose_k99v3`, `pmo_wave75`,
`ipi_aurora`, `titan_t87`, `rog_omni`, and `hid` disassembly text in
`Tools/MouseDock/mousedock-analysis-xKforA`. No reference module was executed or
copied. The source database marks most hardware paths unverified; fixture success
does not establish hardware support. Build/execution is reserved for the main
integration slot. No hardware queries or normal product launch were performed.
