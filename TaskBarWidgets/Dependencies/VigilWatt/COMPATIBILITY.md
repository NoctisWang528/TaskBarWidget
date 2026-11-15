# Admitted built-in protocol plugins

Generated from `PeripheralHost/PluginRegistry.cs` (SHA-256 `fd717c853c5f5f75ec33f4770130aff3b7baf111be9868e09bac4f3eb174593c`),
VigilWatt commit `4df830043296760f5d5c273f4cdc0ea9456c4bc7`.

31 explicit plugin classes are admitted. Hardware acceptance is pending for every entry.

| Plugin class | Family | Compiled source |
| --- | --- | --- |
| `LogitechPlugin` | Logitech | `Plugins/Logitech/LogitechPlugin.cs` |
| `RazerPlugin` | Razer | `Plugins/Razer/RazerPlugin.cs` |
| `RazerKrakenPlugin` | Razer | `Plugins/Razer/RazerKrakenPlugin.cs` |
| `HautePlugin` | Haute | `Plugins/Haute/HautePlugin.cs` |
| `DualSensePlugin` | Sony | `Plugins/Sony/DualSensePlugin.cs` |
| `InzoneH9Plugin` | Sony | `Plugins/Sony/InzoneH9Plugin.cs` |
| `AjazzPlugin` | Ajazz | `Plugins/Ajazz/AjazzPlugin.cs` |
| `AkgPlugin` | Akg | `Plugins/Akg/AkgPlugin.cs` |
| `AsusPlugin` | Asus | `Plugins/Asus/AsusPlugin.cs` |
| `AtkPlugin` | Atk | `Plugins/Atk/AtkPlugin.cs` |
| `AulaG9Plugin` | Aula | `Plugins/Aula/AulaG9Plugin.cs` |
| `AulaPlugin` | Aula | `Plugins/Aula/AulaPlugin.cs` |
| `BetopPlugin` | Betop | `Plugins/Betop/BetopPlugin.cs` |
| `CherryPlugin` | Cherry | `Plugins/Cherry/CherryPlugin.cs` |
| `DarkrimPlugin` | Darkrim | `Plugins/Darkrim/DarkrimPlugin.cs` |
| `DeluxPlugin` | Delux | `Plugins/Delux/DeluxPlugin.cs` |
| `EweadnPlugin` | Eweadn | `Plugins/Eweadn/EweadnPlugin.cs` |
| `GameSirPlugin` | GameSir | `Plugins/GameSir/GameSirPlugin.cs` |
| `GanssPlugin` | Ganss | `Plugins/Ganss/GanssPlugin.cs` |
| `HecatePlugin` | Hecate | `Plugins/Hecate/HecatePlugin.cs` |
| `HecateWirelessPlugin` | Hecate | `Plugins/Hecate/HecateWirelessPlugin.cs` |
| `HyperXPlugin` | HyperX | `Plugins/HyperX/HyperXPlugin.cs` |
| `InphicPlugin` | Inphic | `Plugins/Inphic/InphicPlugin.cs` |
| `LingbaoPlugin` | Lingbao | `Plugins/Lingbao/LingbaoPlugin.cs` |
| `MchoseA5Plugin` | Mchose | `Plugins/Mchose/MchoseA5Plugin.cs` |
| `MchosePlugin` | Mchose | `Plugins/Mchose/MchosePlugin.cs` |
| `MojhonPlugin` | Mojhon | `Plugins/Mojhon/MojhonPlugin.cs` |
| `PulsarPlugin` | Pulsar | `Plugins/Pulsar/PulsarPlugin.cs` |
| `RapooPlugin` | Rapoo | `Plugins/Rapoo/RapooPlugin.cs` |
| `ThunderobotPlugin` | Thunderobot | `Plugins/Thunderobot/ThunderobotPlugin.cs` |
| `VgnPlugin` | Vgn | `Plugins/Vgn/VgnPlugin.cs` |

Behavior limits

- Explicit built-in registry only. Imported source does not imply protocol admission.
- Razer E2/E3/E5/E6: VID1532, usage000C/0001, MI03, feature91. V3 SE00DF keeps its separate codec and exact profile.
- BlackShark0555 is excluded here; product native blackshark_pa fallback implements its audited PA session.
- Logitech HID++ requires valid0003 unit+model identity, with checks before and after battery polling; slots without target identity remain identityOnly/canRead=false. Precise1004 requires capability bit1; voltage/ADC/coarse values are estimated.
- All plugins are request-scoped; there are no retained passive listeners, active probes, device watchers or child processes.
- Discover with nonempty deviceIds requires queryIds and restricts both endpoints and slots. Empty read does not open hardware.
- Receiver containers/serials are not target identity evidence. Direct-body whitelist plus CM unique bus ID gates serial evidence; Haute chip ID scope is adapter.
- DELUX charging and settling frames expose only charging; old percentage/sample time is retained by the parent.
- Synthetic tests do not establish real hardware compatibility.
