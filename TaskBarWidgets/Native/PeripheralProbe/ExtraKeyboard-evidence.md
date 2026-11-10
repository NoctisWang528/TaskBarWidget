# ExtraKeyboard protocol evidence

Scope: 19 assigned keyboard families plus the existing `mchose_qhw` family. The
86 finite target rows come from MouseDock DB179 `devices.json`. Protocol facts
were checked against `内置兼容协议说明.md`, `字节码协议模块索引.md` and the
`dis_wireless_*.txt` files below. This is an independent implementation of wire
facts; vendor program source is not copied or required at runtime.

`extra_keyboard::{KnownIdentity, Admit, Read, OfflineSelfTests}` is unchanged.
`Protocol::ExtraKeyboard` and `Candidate.adapter` identify each decoder. All
edits for this work are confined to this module, target table and this evidence.

| Adapter | Source module(s) | Read-only transaction and response evidence |
| --- | --- | --- |
| `razer_keyboard` | `hid` (`razer_request`, `razer_level`) | Exact `1532:025C`, unique Feature0 descriptor90/native91. `07/80`, transaction `9F`; status2, full header, XOR3..88 at89, trailing zero; battery byte10 scaled from255. |
| `rog_azoth_x` | `azoth_x` | Finite four ROG keyboard IDs, FF00:1, unnumbered64/native65 I/O. `12/01`; header, echo rejection, percent body5 and boolean charge body8. Claymore interface is observed, not an invented pin. |
| `vgn_neon75_air` | `irok` (`neon_decode`) | Exact names, MI1/col01, Feature0 descriptor64/native65. `F7` with raw8=08. Dedicated Neon flags/online guard, body1 percent1..100 and body9 charge. Existing IROK decoder is not reused. |
| `rapoo_v700_input` | `rapoo_v700` | MI2 FF00:2, native10/10/0, reportBD input10. Passive only; BD/01, state7 in1/2, percent8. No initialization or battery output. |
| `cherry_mx2pro` | `cherry_mx2pro` | MI1 FF1C:92, descriptor-selected report4 input/output11..65. `04 00 00 1A 03 00 00 55`; exact response header and nonzero percent8. |
| `vgn_v98pro` | `vgn_v98` | Shared320F:5088 MI1 FF1C:92, exact six model-name aliases. `04 00 00 1A 06 00 00 00`; report4/cmd1A, actual length/padding and initialized payload. No Rainy75 checksum substitution. |
| `atk_v75x` | `atk_v75x` | Shared320F:5088 MI1 FF1C:92, exact ATK/VXE names, report4/64. Nonzero changing16-bit sequence, exact echoed header, no query echo, percent8 and charge9 enum0/1/2. |
| `mchose_g87` | `hid` (`mchose_split_query`, `mchose_g87_status`) | Exact names and explicit generic-name revisions. FF02/FF04 MI1 collections must share current Windows ContainerId, product and revision, include the documented anchor, and provide exactly one report19 input and output. Revalidate both after reading. `13 4A 01`, additive checksum at19; percent5 and flag-nibble guards. Charge stays unknown because of documented false positives. |
| `vgn_v98pro_v3` | `vgn_v98` | Exact names, finite258A IDs, MI1, unique report19 input/output20 with native padding bounded at65. `13 4A 01`, checksum5E at19; reply `13 4A 01 00`, percent5. Charge unknown. |
| `aula_f99pro` | `aula_f99pro` | FA09 MI1 FF02:2, native20/20/0, explicit DB model revision. Two GET05 identities `030000000158`, two GET4A reads, final model identity. Frame length, command and additive checksum at19. |
| `kzzi_k75v2_input` | `kzzi_k75v2` | Same collection geometry, separate explicit model revision. Two GET05 identities `0300000003A2`; two new `13 0A` event05 notifications, final identity. Checksum, event02 link invalidation and no-current-level high nibble10 rejection. No notification initialization/output command. |
| `aula_nova75`, `valkyrie_vk75` | `aula_nova75`, `valkyrie_vk75` | Exact shared-ID names, MI3 FF60:61, descriptor-proven unnumbered32/64. `20/01`, raw32 checksum21; reply `20 01 00`, nonzero percent3. Neutral naming for generic receivers; charge unknown. |
| `weikav_nut75` | `weikav` | Exact NUT75 names and collection. AA10 reads the entire48-byte info buffer, using24+24 chunks for32-byte payloads or48 for64. Match55/10, count and LE address on every fragment. Repeat the entire read; percent17, charge unknown. |
| `aula_hub_keyboard` | `aula_hub_keyboard`, `weikav` | Exact names/usages and board38A6 PID allowlists, including both documented NOVA98 usages. AA10 reads all56 bytes (24+24+8 or56), twice; compare identity bytes4..9 and12..15, percent17, charge18 enum0/1/2. Abort on55FC events4/5/6, including queued/final-drain events. |
| `atk_a87` | `atk_a87` | Finite373B IDs, FF60:61, descriptor0/32 and native33/33/0. `02 96 24` battery. Wireless AA/FD status before each reading and after both; wired profiles skip status. Echo/percent/flag guards; omit numeric value while charging and leave unverified charge indication unknown. |
| `royuan_legacy_status` | `royuan_legacy` | Exact3151:4015 MI2 FFFF:2, unique Feature0/64/native65. FD wireless-service responseAA55 plus zero tail; two matching FB Status24 percentages. Strict busy/online/initialized flags and zero tail. Final service recheck; no F7 fallback. |
| `vgn_v98pro_v4`, `exx_alpha87` | `vgn_v98` | Finite IDs; exact VGN names, FF60:61, unique descriptor-selected numbered64-byte I/O. Command87 with request checksum at63. Validate actual report ID, command, status, length header/range, padding and percent7; EXX charge8 permits00/01/10/11. The source does not establish a response checksum, so none is invented. |
| `mchose_qhw` | `mchose_qhw` | Exact G98/K87 aliases, MI2 FF80:1, unique report4D input/output32 or64. 0900 base identity/online, two0904 battery reads, final stable identity. Match command/nonzero sequence and bounded length. Request XOR at8; source response has payload at8 and no corresponding response-XOR check. DB defaults allow board1015/2025. |

## Bounds and validation

- Every successful transaction requires two effective battery confirmations.
  AA10 confirms two complete assemblies; it never treats a first fragment as a
  complete reading. Identity/status checks surround model-routed reads.
- A read has a10-second overall budget, active exchanges at most1 second,
  AA10 fragments500ms and passive waits2 seconds each. Input loops cap64 calls;
  drains cap32. Progress is emitted before I/O. Feature/report APIs can block
  inside a Windows driver; the owning probe-process watchdog remains required.
- The M HUB resolver caps enumeration256 and same-receiver peers3, verifies
  metadata/descriptors and returns no reading for ambiguity, missing anchor,
  changed revision, inaccessible peer metadata or a changed final channel.
- Explicit `profile_revisions` and generic-name revision restrictions from DB179
  are retained. Sample-only firmware revisions are not admission pins. Missing
  interface/usage values in the source select unique actual report geometry;
  they do not grant other PIDs or guessed report IDs.
- `OfflineSelfTests` exercises every target row, all20 decoders, both AA10 sizes,
  fragment offsets, exact names/geometry, model changes, stale sequences,
  query echoes, truncation, percentage/enum bounds, missing confirmation,
  split-channel ambiguity and final disconnects. It uses pure fixtures and is
  already called by the product probe's offline selftest entry point.
- Authoring checks: all86 rows compared with DB179, duplicate-row and delimiter
  checks, direct whitespace inspection. No compilation, execution of the native
  selftests, product launch or hardware access was performed by this worker.
  Main owns builds and runtime verification; hardware acceptance remains pending.
