# Attached HID repair, 2026-11-10

All hardware operations used the isolated native probe, with battery/status and
identity queries only. No extracted program was executed. Raw paths, serials,
descriptors and full frames remain in ignored `artifacts/diagnostics/hid-repair/`.

## Compatibility changes

| USB identity | Change | Hardware evidence |
| --- | --- | --- |
| `046D:C547` | Admit MI2 FF00:2 long-report collection (report 11, 20-byte input/output); use existing long HID++ requests. First root ping gets 2500 ms; subsequent queries retain 350 ms, inside the existing 18-second total deadline. | User-confirmed G502 X, HID++ 4.2, slot 1, model ID `409FC0980000`; feature 1004 returns 27%, not charging. discover → identify → selected-unit read passed. |
| `1532:0565` | MI3 FF14:1 battery report 2, 64-byte I/O also has Feature report 2 of 9 bytes. Removed the incorrect no-feature condition for this adapter only. Validate successful status, transaction, battery command and payload size. Prefer raw USB product name over generic PnP label. | Two replies per read, 76%; charging remains unknown. |
| `0B05:1A85` | Keep existing unnumbered 64-byte battery protocol; replace changing HID serial metadata with unique USB parent instance serial. Emit wireless connection. | 100%, charging flag true, rediscovery and another read retain the same identity. This PID is the wireless receiver, not wired USB. |
| `0B05:1A83` | Add wired Azoth to the existing `rog_azoth_x` adapter, MI1 FF00:1, native65/65 and descriptor64/64. Emit usb connection. | Physically switched to wired with 1A85 still attached: both exact unique USB parent serials match. One admitted Azoth reads 100%, charging over 1A83, retains the earlier canonical 1A85 identity and carries the receiver's observed legacy alias. |
| `0B05:1C24 / 1C25` | Existing adapter only: mark usb / wireless respectively. | Transport designation from upstream model definitions; not attached. |
| `0B05:1ACE / 1AD0` | Existing `rog_omni_keyboard` adapter only: mark wireless. | No protocol or identity expansion; no Omni receiver attached. |

Discovery now emits admitted battery-capable channels, not every HID collection
belonging to those USB devices. The attached setup returns three channels (Azoth,
Razer, Logitech receiver) instead of ordinary keyboard/consumer/vendor siblings.
Previously configured absent identities still receive explicit unsupported read
responses; the managed owner retains configured entries separately.

## Identity and IPC contract

Azoth 1A83/1A85 metadata uses the exact matching USB ancestor only when Windows
advertises `CM_DEVCAP_UNIQUEID`. Failure falls back to container identity; changing
HID serial suffixes are never trimmed or accepted as stable identity. The serial
key normalizes the model PID to the previously supported `1A85` for both transports; this is a logical key,
not the currently active USB PID. The `connection` field reflects the real path.
Exactly one wired and one wireless channel with the same validated serial are
deduplicated, preferring wired. Different/missing serials never prove a pair.
The optional `legacyIdentities: string[]` contains exact old-format keys built
from raw HID serials captured before replacing them with the validated USB parent
serial. It is emitted for the receiver, or carried onto the preferred wired
candidate before suppressing a uniquely proven wired/radio pair. Managed code owns
alias migration, demand/configuration preservation and cache deduplication. This
does not prove historical corrupt suffixes that are no longer returned; never
match names or trim/guess serial prefixes to migrate those keys. A real 1A83/1A85
pair was subsequently attached and verified: both MI1 FF00:1 descriptors match
the admitted geometry, both exact unique USB parent serials agree, and discovery
emits one wired candidate. The canonical identity equals the earlier wireless
identity byte-for-byte. `wired-switch-descriptors.jsonl`, `wired-switch-frames.log`
and `wired-switch-summary.json` preserve the metadata, selected wired path,
fresh replies and transferred receiver alias.

HID++ source remains `hidpp20`. Receiver discovery emits `receiverProbe: true`.
`identify` emits a child with `receiverProbe: false`,
`discoveryParentIdentity: <receiver identity>`, and
`identity: <receiver identity>|unit=<8 hex digits>|model=<12 hex digits>`.
Identity-only discovery has no battery/timestamp; read the full child identity to
obtain its battery. Selected-child routing and the before/after hardware unit
checks remain intact. The unit/model and returned parent key were verified in the
actual discover/identify/read flow. No additional managed provider is required.

## Frames and decoding

Azoth request: `00 12 01` padded to 65 bytes. Captured 64-byte body begins
`12 01 00 00 00 64 FF 00 01 00 64 0B 11`; body[5] is 100 and body[8] is 1.

Razer request begins `02 00 60 00 00 00 04 00 00 80 21`, padded to 64 bytes,
byte[62]=C7. Captured reply begins
`02 02 60 00 00 00 05 00 80 80 21 01 01 4C`, byte[62]=0A.
The successful response has battery 0x4C=76 at byte[13]. V2 HS flags/checksum
differ from V3: XOR of reply bytes[0..61] is 08, not 0A. Do not import the V3
checksum or zero-flags condition into this adapter. V2 checksum semantics remain
unverified; two fresh correlated battery-command responses are required.

Logitech capability body for feature 1004: `0F 0F 01 ...` (precise percentage
capability present). Battery replies (20 bytes) include
`11 01 06 1F 1B 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00` and
`11 01 06 11 1B 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00`.
The transaction nibble changes, body[0]=0x1B=27, body[2]=0 (not charging).
A cold trace showed request at 31225871 and valid correlated reply at 31227456,
1585 ms later. The former 1000 ms window had already advanced to slot 2 at
31226883. The corrected first window is 2500 ms and keeps exact target, feature,
transaction and ping-echo matching. The failed trace is preserved separately as
`cold-ping-1000ms-failure-frames.log`.
The isolated corrected ping also captured a valid reply after 1118 ms on the
wire. Extending every empty-slot ping to 1000 ms delayed the unit recheck by six
seconds and produced a missing reply; subsequent queries therefore retain the
original 350 ms. Silent attempts while the mouse was idle are retained as failed
diagnostics, not reported as battery observations. After the user woke G502 X,
the focused ping and complete integrated flow passed.

## References and verification

- The supplied AirVolt checkout has Windows/Bluetooth battery providers but no
  implemented vendor HID packet acquisition; it is not packet evidence.
- The supplied MouseDock extraction exists and was read as static JSON/disassembly
  only (`devices.json`, `dis_wireless_azoth_x.txt`, `dis_wireless_hid.txt`). Its
  catalog records prior unstable Azoth serial observations. No extracted code ran.
- [G-Helper Azoth models, pinned source](https://github.com/seerge/g-helper/blob/511d2d2d58f90c1687bfb9bc0cd3593f269180ca/app/Peripherals/Keyboard/Models/Azoth.cs)
  establishes wired1A83, wireless1A85, wired1C24, wireless1C25.
- [G-Helper battery reader, pinned source](https://github.com/seerge/g-helper/blob/511d2d2d58f90c1687bfb9bc0cd3593f269180ca/app/Peripherals/Keyboard/AsusKeyboard.cs)
  corroborates status12/01 and battery/charge offsets. Protocol facts only were
  used; no upstream implementation was copied.

`build-peripheral-probe.ps1 -RunSelfTests` and `tests/test_wire.py` passed. Added
offline cases use the actual three captured descriptors; they cover the split
HID++ collection, Feature9 coexistence, rejected unrelated/invalid reports,
Azoth transport identity and non-merging of different or missing serials.
`artifacts/diagnostics/hid-repair/check_hardware.py` passed the actual discovery,
identify, three selected-device reads and stable Azoth rediscovery flow.
The final run completed in about six seconds at 21:22 Taipei, with fresh readings
at 21:22:54: G502 X 27%/not charging, Razer 76%/charge unknown, Azoth 100%/charging.
All three attached paths report wireless. The managed 20-second identify timeout
fits the unchanged 18-second native HID++ budget.
This describes the earlier wireless run; the later wired-switch run reports
Azoth as usb. The production binary was verified separately from the traced
binary: `production-after-traced-summary.json` passed the native flow, and
`managed-production-wired-report.json` passed the actual compiled managed service
using `TaskBarWidgets.PeripheralProbe.exe`: G502 X 27%, Razer 76%, Azoth usb
100%/charging, plus Windows-provider devices. The production executable matches
`untraced-probe.exe` byte-for-byte.

An intervening managed run identified the correct G502 X child ID but its later
read returned unavailable. The fallback incorrectly inherited the receiver name;
it now uses "Logitech HID++ device". Buffered managed tracing and a subsequent
untraced managed run both succeeded. No foreign software-ID collision was
observed in the captured frames; no software-ID/protocol changes were made on
that hypothesis. Silent idle attempts remain a limitation, not a fresh reading.
Managed cached-child retention applies after a child has actually been identified;
a fresh session cannot invent a hardware unit identity from a silent receiver.
The 22:17 follow-up adds optional HID++ feature 0005 device-name reads, after all
battery and identity checks finish. Function 0 supplies a byte count (1..64);
function 1 reads at most four chunks with advancing offsets. A final hardware
unit/model recheck must succeed before caching the printable name. Failed or
incomplete names do not remove an already confirmed battery sample. The existing
18-second transport deadline and first-ping wake behavior remain unchanged.
Names are cached by the proven unit/model key inside the worker, bounded to 256
entries, reused without extra name queries, and retained for unavailable selected
reads. Managed code should retain a previously proven real name if the same child
ID later receives a generic HID++ mouse/keyboard/device/receiver name, including
after helper restart. No receiver-PID-to-model-name mapping was introduced.

The attached device returned name count 17, followed by ASCII bytes
`473530322058204c4947485453504545` and `44`, yielding `G502 X LIGHTSPEED`.
The name queries and final identity recheck took about 55 ms in the captured
trace. `hidpp-name-traced-frames.log` holds the complete frames. The normal
production helper then returned that name with 25%, not charging, the original
unit/model identity and mouse type at 22:23 Taipei. Its first selected read was
unavailable and the second succeeded; both are recorded in
`hidpp-name-production-selected.json`. This verifies actual name acquisition
without claiming the pre-existing intermittent idle/recheck failures are resolved.
Offline tests cover chunk offsets, cache reuse, oversized counts, optional name
timeouts and name recheck failures preserving battery samples; wire/lifetime
checks also passed.
`final-summary.json`, `final-responses.jsonl`, `final-frames.log` and
`descriptors.jsonl` hold the concrete evidence.

Raw tracing requires both diagnostic compile macro `PERIPHERAL_HID_TRACE` and
explicit `--trace-hid` (or the diagnostic-only `--diagnose-c547-ping`, which sends
one long slot-1 ping without sweeping other slots). The diagnostic build also
accepts explicit `TASKBARWIDGETS_HID_TRACE_FILE` to buffer frames and IPC requests
to the named file, flushing between requests so a managed diagnostic can capture
its actual sequence without per-frame disk flushes. With that variable unset it
opens no trace file. Without the compile macro `tests/HidTrace.h` contributes no
wrappers, globals, branches or environment reads; production calls hidapi directly.
The optional `--inspect-hid` entry point reads metadata/descriptors only.
No normal product launch, CUA, full product build, installation, configuration
write, pairing, push or release was performed. Physical wired+radio deduplication
was accepted during this session; alternative firmware, Omni receivers and
full unplug/replug acceptance remain untested.
