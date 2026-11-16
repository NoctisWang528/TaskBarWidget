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

## 2026-11-15 Omni paired model display

Omni discovery previously retained the Windows vendor-defined HID collection
name. The successful read already verified the paired model before and after
the battery requests, but its `Reading.peer` metadata was discarded by the
display path. `ConfirmedModelName` now derives display metadata from that exact
decimal paired-model field. It requires the Omni adapter, supported receiver,
and a model proven by matching paired-identity replies. This proof can survive
an explicit negative battery reply without claiming a fresh battery reading.
It never uses the receiver product string or receiver PID as a keyboard model,
changes identities, or expands protocol admission. Until a confirmed model,
discovery uses `ROG Omni keyboard`.

The model/PID facts come from the independently maintained ROG-Battery device
table at commit `0c2b51ba96c7ad537f27339f73a2969c4c144b90`:
[pinned devices.rs](https://github.com/youmikk/ROG-Battery/blob/0c2b51ba96c7ad537f27339f73a2969c4c144b90/src/devices.rs#L76-L106).
Only name/PID facts were used; no source implementation was copied. `1B42`
names AZOTH EXTREME; `1B06` is not assumed to be that model. Existing supported
model IDs without an independently confirmed display name retain the neutral
fallback. A name mapping also cannot authorize a model on a receiver which
the existing Omni protocol allowlist excludes.

Offline regression cases cover the legacy paired-model packet `1B42`, its
standard battery field, the resulting AZOTH EXTREME name, zero battery,
receiver-only metadata, missing identity proof, unmapped models,
modern/legacy receiver boundaries and unrelated adapters. Main integration
owns execution and actual-hardware acceptance; these fixtures do not by
themselves establish hardware support or charging semantics.

## 2026-11-15 paired model while battery is unavailable

On the user's `1ACE` receiver plus 8K booster, a five-read trace consistently
returned paired model `1CF1`, report 2/type 4, followed by a 64-byte `02 FF AA`
negative reply to the existing `02 12 01` battery request. The old reader waited
for a further timeout and discarded the already known model: five attempts
took 1051–1062 ms and displayed the neutral name. The receiver's selected report
matched the paired-list route; this did not establish a routing failure.

The updated transaction ends the power query immediately on the exact negative
report, then confirms the paired model again and revalidates the HID channel.
If the model agrees, it returns `unavailable` with model metadata and no battery
or charging value. A timeout, mismatched model or invalid response cannot grant
that metadata. Two effective power replies are still required for a fresh
battery reading; a later negative reply discards the earlier provisional value.
Five subsequent real-device reads reported `ROG AZOTH EXTREME SPECIAL EDITION`
in 46–51 ms, all without a fabricated battery value. The hardware was still
rejecting battery requests, so this is not evidence that its current battery
became readable, nor proof that its exact state was sleep rather than another
temporary unavailable state.

The negative acknowledgement fact is corroborated by
[ROG-Battery's pinned HID implementation](https://github.com/youmikk/ROG-Battery/blob/0c2b51ba96c7ad537f27339f73a2969c4c144b90/src/hid.rs#L20-L23).
The paired-list route matches the documented packet facts in
[RIGStats' pinned Omni reader](https://github.com/dvalfrid/rigstats/blob/63e2297f03c344a5260b2bc5398f55e4dd0280b2/sensor-sidecar/Control/Lighting/OmniMouse.cs#L90-L110).
Only protocol facts were used, without copying their implementation. A temporary
read-only presence diagnostic was removed; the production reader adds no new
hardware command, pairing change, forced wake, lighting write or driver change.

The pure transaction fixtures cover explicit NAK, the exact report size/channel,
paired-model change during a query, timeout, successful two-reply battery reads
and a NAK after the first provisional value. Native offline tests, the managed
pipe contract and 50 battery-service tests passed. The managed cache records the
confirmed model separately from its display name: same-model unavailable reads
retain the last real value, while a newly proven different model cannot inherit
the previous model's battery. A valid paired-model response keeps the existing
normal demand interval; actual query failures retain the existing backoff.
Raw traces remain under ignored local artifacts and are excluded from packages.
