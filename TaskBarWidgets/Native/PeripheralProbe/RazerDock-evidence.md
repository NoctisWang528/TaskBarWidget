# Razer Dock V2 Pro battery identity

Scope: `1532:00F1`, MI 3, usage page `000C`, usage `0001`, no Input/Output reports,
Feature report 0 with 90 descriptor bytes / 91 native bytes. Captured descriptor:
`050c0901a1010600ff0902150025017508955ab102c0`.
The receiver itself is not a battery device. Only a verified downstream unit is exposed.

## Read-only requests

All commands use transaction `1F`, 91 native bytes, report ID 0. Native byte 6 is
declared payload size, bytes 7/8 are class/command. The request checksum is XOR of
bytes 3 through 88, stored at byte 89. Unused outgoing bytes are zero.

| Class / command | Declared size | Verified response |
| --- | --- | --- |
| `00 / BF` | 31 | `02 01 00 E3 00 FF FF` followed by zeroes: two slots, first connected as `00E3`, second empty. |
| `00 / 82` | 22 | Exactly 15 ASCII alphanumeric serial characters for the paired mouse. The actual serial is private and is not recorded here. |
| `07 / 80` | 2 | `00 94`: 148/255, rounded to 58 percent. Charging is unknown. |

These responses were observed on the attached Dock V2 Pro with a Basilisk V4 Pro
on 2026-11-15. The raw local, non-distributable capture is
`artifacts/checks/peripheral-fix/dock-routed-query.txt`. This does not establish
support for other dock models, downstream models, or mouse-plus-keyboard pairing.

The transport initially waited 100 ms and received unrelated `0F/03` RGB traffic.
Immediate reads followed by bounded short waits captured the requested replies
14–16 ms after the first send. Replies must match status 2, transaction, command,
declared length and CRC; busy, timeout, errors and unrelated RGB are not battery
readings. The caller bounds retries and the entire exchange, without stopping or
changing the user's RGB software.

## CRC and shared-buffer padding

The firmware leaves RGB bytes in unused parts of the returned buffer. The actual
checksum still matches the response header plus meaningful fields, with the
unused bytes treated as zero:

- `07/80`: checksum `11` matches the header and the two battery bytes. XOR across
  all returned bytes instead gives `16` because unused bytes contain RGB data.
- `00/82`: checksum `DE` matches the header and the 15 serial characters. Bytes
  after those characters, even inside the legacy declared size 22, can contain
  unrelated RGB bytes.
- `00/BF`: checksum `40` matches the header and all 31 payload bytes in the capture.

This normalization applies only to the three commands of the exact admitted
Dock V2 adapter. The usual Razer mouse decoder keeps its existing full-report
checksum rule. Offline tests corrupt each meaningful response, including the
serial and battery, to prove that normalization does not bypass CRC validation.

## Identity and bounds

The paired list must exactly prove one connected `00E3` mouse and an empty second
slot. A non-placeholder 15-character serial must differ from the dock's serial.
Both list and serial are read again after the optional battery query. Changed or
unavailable identity discards the result. The public child identity is the exact
receiver identity plus `|unit=pid=00e3;serial=<hex of actual serial bytes>`; a slot
or display name never becomes identity. Failed battery validation can preserve a
proven unit with unavailable battery, never a synthesized zero. Five commands
maximum are issued for a battery read, four for identity-only discovery.

## Primary references

- [OpenRazer Basilisk V4 Pro addition, PID 00E3](https://github.com/openrazer/openrazer/pull/2975/files)
- [OpenRazer command definitions](https://github.com/openrazer/openrazer/blob/master/driver/razerchromacommon.c): `00/82`, `07/80` and the 0–255 battery scale.
- [OpenRazer command research](https://github.com/openrazer/openrazer/wiki/Unknown-commands): `00/BF` size 31 and slot/status/PID structure.
- [SignalRGB Razer protocol research](https://gitlab.com/signalrgb/signal-plugins/-/blob/master/Plugins/Razer/Razer_Info_Finder.js): `getDeviceTransactionID` treats 15 returned characters as a unit serial. Its alternate `00/BF` size 7/transaction `0C` was only researched; the implementation uses the locally captured `1F`/31 combination.
- [MacRazer device catalog](https://github.com/SorcRR/MacRazer/blob/master/Sources/MacRazer/RazerDevices.swift): a Dock V2 Pro/Basilisk V4 Pro battery report on transaction `1F`.

Protocol facts inform the implementation; upstream application code is not copied.
