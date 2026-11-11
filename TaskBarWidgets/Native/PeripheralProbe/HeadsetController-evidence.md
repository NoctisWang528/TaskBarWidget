# Headset/controller battery adapters

Owned implementation: `HeadsetController.h`, `HeadsetController.cpp`,
`HeadsetControllerTargets.inc`, this evidence file. No catalog, central protocol,
probe, build script, Media, UI or dependency edits are included.

Contract: `peripheral::headset_controller::{KnownIdentity,Admit,Read,OfflineSelfTests}`.
Central `Protocol::HeadsetController`, `Candidate.adapter` / `usbProduct`, and
`Reading.role` / `components` are required. Central classification still selects
one unambiguous collection and revalidates it before dispatch. `Read` opens its
own RAII HID handle, checks open-handle identity, descriptor, native caps and PnP
container again, and never uses a name or enumeration ordinal as device identity.

## Evidence and coverage

Source: MouseDock DB179, `devices.json`, `内置兼容协议说明.md`, the following
`dis_wireless_*.txt` static disassembly files. These are independent implementations
of protocol facts; no vendor executable or Python bytecode is executed, and no
HeadsetControl source is copied or linked. Hardware acceptance remains pending.

The target table contains 67 finite rows covering all 33 in-scope complete-evidence
family keys below. Missing page/usage pins in a source profile are resolved by
the source adapter's collection/report constraints, not a vendor-wide grant.

| Family keys | Evidence file(s), with `dis_wireless_` prefix | Battery-only transport and reply guards |
| --- | --- | --- |
| atk_n9v2, mchose_v9turbo | mchose_headset.txt, hid.txt | 55/65; exact identities and vendor-page sets; report55/64. Missing Output descriptor exception only on FF90:1, native64/64, no contradictory Output reports. No report41 fallback. |
| dailing_g3, ikf_legacy | hid.txt, ikf_legacy.txt, mchose_headset.txt | 55/65, exact MI/usage; independent status enums. DAILING supports documented zero padding and the exact bcd0100 native65 unnumbered route. iKF legacy's charging enum is preserved. |
| mchose_frame | mchose_headset.txt | AA/0B request; report55, command/length fields, XOR and online guard. |
| mchose_v9pro | hid.txt | Exact V9 PRO product, MI0, revisions0003/0012/0013, I/O64. State2 gives capacity; states0/3 mean external power with frozen capacity hidden; 26 unavailable. |
| vgn_siren, vgn_zhou | hid.txt, mchose_headset.txt | Siren Report2/0B, ten-step estimates; F779 alone supports Input2/13 with Output2/64 or65. Zhou requires actual Report55/64, never a 32-byte remap. |
| alpha2, jbl910, aula_g9 | hid.txt | Alpha 50/02 ->51/02 with charge flag; JBL passive report8; G9 passive AB/GM86 status with online proof. Passive families have no query packet. |
| blackshark2020 | blackshark2020.txt | FeatureFF/64, exact source profile; two fresh-header responses, stale-header rejection; zero voltage offline, coarse estimates, unreliable charging capacity suppressed. |
| razer_headset | hid.txt | Feature0/native91; transaction3F, 07/80, exact status/header/length/XOR. |
| blackshark_v2_hs, blackshark_v3_pro | hid.txt | Control SET_REPORT2/64, battery8021; PS101B also link0020 with before/after checks. V3 Pro status, lengths and XOR validated. No capability/RF-wake initialization. |
| blackshark_pa, barracuda_pa | halo.txt | Separate PA08/PI08 routes and command21 type/length guards. E1 is temporary remote read routing; it is restored on success and failure, including budget exhaustion. No audio configuration. |
| cloud2, cloud3 | halo.txt, hid.txt | Cloud II 06/FF/BB/02 Output query and 20-byte status; Cloud III report66/89, descriptor-selected Output or Feature route, correlated66/89 or66/0D reply. |
| cloud3s | cloud3s.txt | Report0C commands06 and48; unique matching input/output collection. Split input chosen by exact VID/PID/revision/MI, source usage tuple, actual report12 geometry and same nonempty PnP container. Ambiguous input peers rejected. |
| cloud_flight2 | flight2.txt | Exact MI3 Col04; commands52/01 and50/02 only. Declared reports, correlated FF ACK, bounded busy retries, connection status, battery and charging parsed. Connection-one exception limited to documented revisions/caps. |
| hecate_g5max, hecate_gx03 | hecate.txt | Col03, actual D0 geometry, D0/35/BB/01/00 only; connection/length guards. GX03 emits only present left/right components; no invented case or charging. Includes A220 G3 wireless. |
| ikf | ikf.txt | Input3 plus Output2 or Feature3; CRC16 link response, randomized app sequence, EF identity PDID/MID confirmation before/after two battery reads; FA/0C only. Wired state rejected; charging unknown. |
| moondrop_gm | moondrop.txt | Descriptor-selected4B, link31 and battery27; two battery replies bracketed by live link checks. |
| epos_h3pro | epos.txt | Report1 remote route5 battery GET, payload12/00/03/00; fixed envelope, command, length and CRC8. Charging unknown. |
| sony_ds4, sony_dualsense | controller_hid.txt | Passive USB64 or BT78 reports, correct offsets, reserved-value rejection and BT CRC32 seedA1. DS4 adapter connection bit checked. Battery bands marked estimated. |
| eightbitdo_input | controller_hid.txt | Passive exact product list; declared report4 USB/report1 Bluetooth; battery low7/charge highbit, unavailable zero rejected. No XInput ordinal matching. |
| nintendo_bt | controller_hid.txt | Bluetooth only; passive21/30, battery nibble bands. If passive listening yields nothing, documented device-info subcommand02 may be sent once through the parent journal callback. No report-mode or pairing changes. |
| flydigi_apex5, flydigi_vader5 | flydigi.txt | GET_INFO only; separate numbered/unumbered descriptors, product-ID guards, known band/status fields. Preserve x/5 bands; firmware metadata is not interpreted as capacity or used to reject known GET_INFO replies. |
| leadjoy_zhiwan | leadjoy.txt | MI4 FF80:1 unnumbered64/native65; A5/CC JSON command14 only, bounded length/checksum, strict numeric fields, duplicate-key rejection at every object. Physical-container identity, never an XInput index. |

## Existing coverage and actual evidence gaps

- Sony H5 `054C:0EBF` is already in the audited
  `Media/Peripherals/HeadsetControlDevices.cs` extension allowlist. It is deliberately
  excluded here; its manifest classification should be **extension**, not a
  duplicate native implementation. The extension is optional and disabled by default.
- Existing native Sonix, Xiberia T10/T7, BlackShark V3 passive `1532:057A`,
  ROG PELTA/Delta II, Sony H9/H7 and GAIA remain with their existing adapters.
- Zhou `372E:10BB` observed native32/32/16 consumer collection has no proven
  Report55 mapping. It remains identity-only for that layout, even though a
  future descriptor-proven Report55/64 collection can use this adapter.
- GX03 case capacity, HECATE/iKF-modern charging, and unknown status/charging
  enums remain unknown. Shared `040B:F779` cannot prove a unique marketing model.
- No complete-evidence headset/controller family is deferred for implementation
  time. Unsupported geometries, ambiguous peers and incomplete identity proof
  fail closed. Mouse and keyboard protocols belong to other owners.

## Validation and runtime bounds

`OfflineSelfTests()` is embedded in the owned CPP. It creates only byte arrays and
synthetic candidates; it does not enumerate, open HID, run a vendor tool or start
the product. Fixtures cover every target row, rejected IDs/interfaces/usage/
descriptors, packet echoes and corruption, all decoder families, Sony BT CRC,
iKF CRC/sequence/identity envelopes, Flight2 ACK ordering and busy statuses,
DAILING padding/unnumbered routes, F779 short status, GX03 component roles and
once-only callback ordering even after a failed write.

Read budget is seven seconds including local metadata work; individual waits,
input draining and retries are bounded. Driver calls still rely on central
worker timeout/quarantine protection. The Nintendo bootstrap journal is consumed
before the write and is never retried after failure, refresh or reconnect. All
other traffic consists of documented read requests or passive input; the existing
BlackShark once-initialization path is not duplicated.

No compiler, build, executable, hardware query or normal product launch was run
by this worker. Main owns compilation and execution of the integrated offline tests.

## 2026-11-11 BlackShark V2 HS powered-off receiver fix

Scope: `1532:0565`, MI3, FF14:0001, Report2/64 (Feature9 retained).
Only `HeadsetController.cpp` and this evidence record changed for this fix;
regressions use its existing `OfflineSelfTests()` entry point.

Static upstream evidence was read from MouseDock DB179 `dis_wireless_hid.txt`:
`blackshark_pro_request/reply` (original Python lines 303-323) defines read-only
0020 link state with zero offline / one online; `blackshark_hs_read/level`
(lines 349-373) only queries 8021 capacity and propagates timeouts. It does not
prove the V2 HS link envelope. That envelope is established by the following
local hardware capture, not by assuming the V3 Pro format.

With the headset powered off and its receiver still attached, three 8021 reads
produced no input and the old probe returned `unavailable`. Three alternating
0020 / 8021 diagnostic rounds then showed a 64-byte successful 0020 reply
within 3-4 ms each time; 8021 remained silent over each 1500-ms observation.
No initialization, pairing, lighting, volume, firmware or configuration commands
were sent. Only control SET_REPORT read requests 0020 and 8021 were used.

Exact 64-byte link query:
```
02006000000004000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004600
```
Exact 64-byte powered-off reply (identical in all three rounds):
```
0202600000000500800020010100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c700
```
V2 HS has success byte1=02, flag byte8=80, command0020, payload length1,
payload byte13=00, and byte62=C7 = XOR(bytes1..61), excluding the report ID.
The previously captured successful 8021 capacity76 reply also satisfies this
checksum rule (byte62=0A). V3 Pro's length2/flag/checksum assumptions do not apply.

The decoder now correlates the requested command and validates the full known
header, route, payload length, padding and checksum. Only a successful validated
0020 payload00 becomes `disconnected`, without a live battery. 0020 payload01
allows capacity reads; link checks bracket each capacity round. Empty input,
query errors, unknown enums, corrupt/foreign frames and timeouts remain unknown.
An 8021 battery value zero remains an online zero-percent battery.

Validation in isolated `artifacts/airpods-20261111/razer/`:
- Product probe Release build and embedded offline selftests passed.
- Existing `tests/test_wire.py` offline IPC/lifetime checks passed.
- Production and traced probes: three consecutive real-device reads each returned `disconnected`
  with an empty battery array.
- New offline fixtures cover the actual offline packet, command correlation,
  empty input, request echoes, header/checksum/padding corruption, checksummed
  query error, truncated input, unknown link enum, synthetic link-up and zero
  battery. The earlier real capacity76 fixture remains passing.

Local raw evidence: `off-baseline-frames.log`, `off-link-frames.log`,
`off-fixed-production-responses.jsonl`, `off-fixed-traced-frames.log` and
`off-fixed-traced-responses.jsonl`. Final trace has only three 0020 requests and
three explicit offline replies; no battery query is sent after link-down. Diagnostic source `query-link.cpp` uses
existing `tests/HidTrace.h`; its only commands are 0020 and 8021. These artifacts
are local diagnostics, not release content.

Boundary: powered-on link01 and native probe recovery are now verified by the
follow-up below. Live service/card recovery remains an integration acceptance item.
The normal product was not started, and live card filtering/74% cached-history
presentation is left to the main integration task. No installation or settings
were modified. Existing shared service code can retain historical capacity;
this fix supplies the explicit disconnected state required by HideDisconnected.

### Powered-on follow-up, 2026-11-11

After the user powered on the same headset, the already-built isolated production
probe was queried once (one discover and one selected-device read). It returned
`online`, main battery **74%**, `estimated=false`, charging unknown, error empty,
and fresh `updatedAt=2026-11-11T00:00:21.494Z`. This establishes native probe
recovery from the earlier explicit disconnected state on the same stable identity;
it is not a cached capacity returned by the service. The normal read internally
performs its existing two battery rounds with before/after link checks.

A second, single-command read-only diagnostic used the existing HidTrace helper
and sent exactly one 0020 request, with no 8021, retry or initialization. Its
successful 64-byte reply arrived in **3 ms**:
```
0202600000000500800020010101000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c600
```
The online payload byte13=01 and checksum byte62=C6 match the new decoder.
The off/on replies differ only at byte13 (00 -> 01) and byte62 (C7 -> C6),
confirming the previously synthetic V2 HS link-up interpretation on hardware.

Local evidence: `artifacts/airpods-20261111/razer/on-production-responses.jsonl`
and `on-link-once-frames.log`; bounded capture source is `query-link-once.cpp`.
Exactly two selected-device query invocations were used: one production read,
then one single-command 0020 capture. The production request seeded
`attemptedInitializations` to prohibit reinitialization; the returned `initialized`
array echoes that seed, and no initialized event was emitted. No normal product,
installation/configuration mutation or additional hardware retry was performed.
Only this evidence document changed in tracked source during this follow-up.
