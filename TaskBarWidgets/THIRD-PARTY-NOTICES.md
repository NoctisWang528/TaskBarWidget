# Source attribution

Binary distributions include the product `LICENSE` and `NOTICE`, this file, and the consolidated notices under `licenses/`. License texts are grouped by license family; each affected component, its source and its copyright are identified in the corresponding file.

Quick-card liquid glass adapts the circular lens and rounded-edge highlight profiles from [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass/tree/eb4a0ea2e17542ea28c2e71088f7dd9acf77aad5), Copyright 2025 Kyant, under Apache-2.0. The license text, source and modification notices are in [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt). The native Windows renderer does not bundle Compose or Android binaries.

Peripheral battery acquisition uses [HIDAPI 0.15.0](https://github.com/libusb/hidapi/tree/hidapi-0.15.0), under its permitted BSD license option; see [licenses/BSD.txt](licenses/BSD.txt). The native acquisition helper links this pinned source and does not load arbitrary external plugins.

[HeadsetControl 4.1.0](https://github.com/Sapd/HeadsetControl/tree/4.1.0) is distributed as a separate, optional, unmodified command-line program under GPL-3.0, disabled by default. The package contains its license, upstream provenance and corresponding source archive together under `Dependencies/HeadsetControl/`. The application invokes only its audited battery query with standard text output; it does not link HeadsetControl into the product. AirVolt was consulted for feature ideas only; no AirVolt source or assets are included.

TaskBarWidgets integrates source from the reference snapshot in workspace commit `a0783de5a245d11d830882ccb5b79964cf1d53af`. Modifications belong to this product directory; the reference trees remain unchanged. Original author names and license notices are preserved. This document does not relicense third-party code.

| Product content | Source | Notice |
| --- | --- | --- |
| Native taskbar, monitoring, plugin API and hardware wrapper | zhongyang219/TrafficMonitor, workspace `TrafficMonitor/` | [Anti 996 license, English and Chinese](licenses/Anti-996.txt) |
| Lyrics core, media session services, Web rendering, settings and associated tests | ANYNC/TaskbarLyrics, workspace `TaskbarLyrics/` | [MIT license](licenses/MIT.txt) |
| Audio endpoint policy switching and related behavior | Fervent-Tempo/AF-Media-Bar, workspace `AF-Media-Bar/` | [MIT license](licenses/MIT.txt); upstream snapshot notice below |

Native runtime dependencies include LibreHardwareMonitor and the copied OpenHardwareMonitorApi wrapper. The unchanged `LibreHardwareMonitorLib.dll` reports version `0.9.4+b8077435b898d57539956388cddf49f7dacb86f7`; its corresponding source is available at the [pinned upstream commit](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/b8077435b898d57539956388cddf49f7dacb86f7), under the Mozilla Public License 2.0 reproduced in [licenses/LibreHardwareMonitor.txt](licenses/LibreHardwareMonitor.txt). Its embedded WinRing0 and InpOut components retain their notices in [licenses/BSD.txt](licenses/BSD.txt). Earlier versions bundled Source Han Sans; current builds use system fonts, so that historical SIL Open Font License text is no longer shipped and remains only in repository history.

## Built-in monitoring sources

The native executable compiles these three providers directly. `ITMPlugin` is an internal adapter retained to preserve display-item IDs, layout ordering and rendering behavior; no plugin DLL is scanned or loaded.

| Feature | Pinned source | Local adaptation |
| --- | --- | --- |
| Battery | [zhongyang219/TrafficMonitorPlugins, eb7fc9e56f93fb69c99b185c6b8c395153d78bc6](https://github.com/zhongyang219/TrafficMonitorPlugins/tree/eb7fc9e56f93fb69c99b185c6b8c395153d78bc6/Plugins/Battery) | Keep `b5R30ITQ`; fixed legacy config filename; private type names and resource IDs; host-clock animation and GDI cleanup. |
| PowerMon | [AzulEterno/PowerMonPlugin-For-TrafficMonitor, b4440c089f25ea1be38843527152185f7831288e](https://github.com/AzulEterno/PowerMonPlugin-For-TrafficMonitor/tree/b4440c089f25ea1be38843527152185f7831288e/PowerMonPlugin) | Keep all eight IDs and battery-driver/formatting source; CPU/GPU/smart items share the product hardware monitor instead of initializing another CLR computer. |
| WeatherPro | [Haojia521/TrafficMonitorPlugins, weather-pro-v0.10, 49c68beb43648d599b593114b519269e40be411e](https://github.com/Haojia521/TrafficMonitorPlugins/tree/49c68beb43648d599b593114b519269e40be411e/WeatherPro) | Match the installed 0.10 config and `FxOP34Lm` ID. The [successor repository](https://github.com/Haojia521/TMP-WeatherPro) changes config semantics, so this migration pins the compatible source. WinHTTP supplies bounded requests, response size and gzip decoding; one owned worker replaces detached threads. |

Only display resources are embedded. Plugin options windows and update/download controls are omitted; allowlisted options are edited through unified Hardware settings. Active configuration lives in `Data/config/battery.ini`, `power.ini` and `weather.ini`; legacy `plugins/Battery.dll.ini`, `PowerMonPlugin.dll.ini` and `WeatherPro.ini` names remain migration inputs only. Weather credentials never enter IPC and are stored as current-user DPAPI ciphertext outside the INI.

Battery, PowerMon and WeatherPro are MIT-licensed, Copyright (c) 2021 zhongyang219; see [licenses/MIT.txt](licenses/MIT.txt). `Weather/SimpleIni.h` carries its MIT notice, and `utilities/yyjson/yyjson.[ch]` carries its MIT notice; both are consolidated there.

## Managed and Windows App SDK dependencies

Inventory of runtime/native assets resolved by `Media/obj/project.assets.json`, `Lyrics.Core/obj/project.assets.json`, and `UI/obj/project.assets.json` for Windows x64. Build-only/reference packages and placeholder assets are omitted. Package authors, copyrights and license declarations come from the local NuGet metadata; vendor-file terms are not relabeled as an SPDX license. Components that share identical license bytes share one consolidated file.

Lyricify.Lyrics.Helper is built from the vendored 0.2.0 source snapshot, not the former NuGet binary: Apache-2.0 license and source/modification notice in [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt). CHTCHSConv/ChineseConverter is excluded; the fork uses the listed OpenCC dependency. Its upstream parser acknowledgments are retained in that notice.

| Package and resolved version | Authors | License / SPDX | Copyright notice | Source | Consolidated terms |
| --- | --- | --- | --- | --- | --- |
| F23.StringSimilarity 7.0.1 | James Blair, Paul Irwin | MIT | Copyright 2018 feature[23] | [source](https://github.com/feature23/StringSimilarity.NET/tree/faaecccaaf58415a32af648f424b522dcacd365b) | [MIT.txt](licenses/MIT.txt) |
| Microsoft.Data.Sqlite.Core 8.0.8 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/efcore/tree/90d079985f33ae91c05b98ecf65e0ce38270ba55) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.EntityFrameworkCore.Abstractions 8.0.8 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/efcore/tree/90d079985f33ae91c05b98ecf65e0ce38270ba55) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.EntityFrameworkCore.Relational 8.0.8 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/efcore/tree/90d079985f33ae91c05b98ecf65e0ce38270ba55) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.EntityFrameworkCore.Sqlite.Core 8.0.8 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/efcore/tree/90d079985f33ae91c05b98ecf65e0ce38270ba55) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.EntityFrameworkCore 8.0.8 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/efcore/tree/90d079985f33ae91c05b98ecf65e0ce38270ba55) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Caching.Abstractions 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Caching.Memory 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Configuration.Abstractions 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.DependencyInjection 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.DependencyModel 8.0.1 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/2aade6beb02ea367fd97c4070a4198802fe61c03) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Logging.Abstractions 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Logging 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Options 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Extensions.Primitives 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| Microsoft.Web.WebView2 1.0.3719.77 | Microsoft | Vendor terms (see text) | © Microsoft Corporation. All rights reserved. | [source](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77) | [Microsoft-WebView2.txt](licenses/Microsoft-WebView2.txt) |
| Microsoft.WindowsAppSDK.Base 2.0.4 | Microsoft | Vendor terms (see text) | © Microsoft Corporation. All rights reserved. | [source](https://aka.ms/windowsappsdk) | [Microsoft-WindowsAppSDK.txt](licenses/Microsoft-WindowsAppSDK.txt) |
| Microsoft.WindowsAppSDK.Foundation 2.3.12 | Microsoft | Vendor terms (see text) | © Microsoft Corporation. All rights reserved. | [source](https://aka.ms/windowsappsdk) | [Microsoft-WindowsAppSDK.txt](licenses/Microsoft-WindowsAppSDK.txt) |
| Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.9 | Microsoft | Vendor terms (see text) | © Microsoft Corporation. All rights reserved. | [source](https://aka.ms/windowsappsdk) | [Microsoft-WindowsAppSDK.txt](licenses/Microsoft-WindowsAppSDK.txt) |
| Microsoft.WindowsAppSDK.Runtime 2.5.1 | Microsoft | Vendor terms (see text) | © Microsoft Corporation. All rights reserved. | [source](https://aka.ms/windowsappsdk) | [Microsoft-WindowsAppSDK.txt](licenses/Microsoft-WindowsAppSDK.txt) |
| Microsoft.WindowsAppSDK.WinUI 2.3.9 | Microsoft | Vendor terms (see text) | © Microsoft Corporation. All rights reserved. | [source](https://github.com/microsoft/windowsappsdk) | [Microsoft-WindowsAppSDK.txt](licenses/Microsoft-WindowsAppSDK.txt) |
| NAudio.Core 2.2.1 | Mark Heath | MIT | © Mark Heath 2023 | [source](https://github.com/naudio/NAudio) | [MIT.txt](licenses/MIT.txt) |
| NAudio.Wasapi 2.2.1 | Mark Heath | MIT | © Mark Heath 2023 | [source](https://github.com/naudio/NAudio) | [MIT.txt](licenses/MIT.txt) |
| Newtonsoft.Json 13.0.3 | James Newton-King | MIT | Copyright © James Newton-King 2008 | [source](https://github.com/JamesNK/Newtonsoft.Json/tree/0a2e291c0d9c0c7675d445703e51750363a549ef) | [MIT.txt](licenses/MIT.txt) |
| OpenCC 0.1.0 | Will 保哥 | MIT | Copyright (c) 2026 Will 保哥 | [source](https://github.com/doggy8088/OpenCC/tree/aadb7e2e1721acfeaba22f58dae49492cc2c11f7) | [MIT.txt](licenses/MIT.txt) |
| SharpZipLib 1.4.2 | ICSharpCode | MIT | Copyright © 2000-2022 SharpZipLib Contributors | [source](https://github.com/icsharpcode/SharpZipLib/tree/33f64eb0f28cdd2b084cb822fcc224c7c5aba553) | [MIT.txt](licenses/MIT.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 2.1.6 | Eric Sink | Apache-2.0 | Copyright 2014-2023 SourceGear, LLC | [source](https://github.com/ericsink/SQLitePCL.raw) | [Apache-2.0.txt](licenses/Apache-2.0.txt) |
| SQLitePCLRaw.core 2.1.6 | Eric Sink | Apache-2.0 | Copyright 2014-2023 SourceGear, LLC | [source](https://github.com/ericsink/SQLitePCL.raw) | [Apache-2.0.txt](licenses/Apache-2.0.txt) |
| SQLitePCLRaw.lib.e_sqlite3 2.1.6 | Eric Sink | Apache-2.0 | Copyright 2014-2023 SourceGear, LLC | [source](https://github.com/ericsink/SQLitePCL.raw) | [Apache-2.0.txt](licenses/Apache-2.0.txt) |
| SQLitePCLRaw.provider.e_sqlite3 2.1.6 | Eric Sink | Apache-2.0 | Copyright 2014-2023 SourceGear, LLC | [source](https://github.com/ericsink/SQLitePCL.raw) | [Apache-2.0.txt](licenses/Apache-2.0.txt) |
| System.Text.Encodings.Web 8.0.0 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/5535e31a712343a63f5d7d796cd874e563e5ac14) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |
| System.Text.Json 8.0.4 | Microsoft | MIT | © Microsoft Corporation. All rights reserved. | [source](https://github.com/dotnet/runtime/tree/2aade6beb02ea367fd97c4070a4198802fe61c03) | [Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt) |

Microsoft.Web.WebView2 1.0.3719.77 is a transitive runtime dependency of Microsoft.WindowsAppSDK.WinUI 2.3.9: the product does not create WebView2 controls or use its web-page rendering feature, but the published UI still contains Microsoft.Web.WebView2.Core.dll, Microsoft.Web.WebView2.Core.Projection.dll and WebView2Loader.dll, so the package's original license and notices are reproduced in [licenses/Microsoft-WebView2.txt](licenses/Microsoft-WebView2.txt).

Lightweight packages require system-installed x64 .NET 8 Desktop Runtime and Windows App Runtime 2.5.1 or compatible later 2.x. Their framework binaries are not bundled; app-local managed projections and the bootstrap library remain. The .NET license and third-party notices for the app-local assemblies are consolidated in [licenses/Microsoft-DotNet.txt](licenses/Microsoft-DotNet.txt). Native VC143/Unicode MFC runtime files retain the Microsoft terms associated with the redistributable supplied by Visual Studio.

Components removed from the product (WPF-UI, Windows ML, System.Numerics.Tensors and unused AI/ONNX packages) no longer ship license or notice files; their provenance remains in repository history.

## Native parsers

WeatherPro's bundled `SimpleIni.h` and `yyjson` parser preserve their embedded MIT notices. Binary distributions include the consolidated MIT text and the original yyjson header notice in [licenses/MIT.txt](licenses/MIT.txt), and the [tinyxml2 source notice](licenses/Other.txt).

## Lyrics helper

The lyrics helper is built from a pinned source fork: the Apache-2.0 license, pinned upstream commit, local modifications and parser acknowledgments are in [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt). The source fork uses OpenCC and does not distribute CHTCHSConv/ChineseConverter.

## Heatmap reference

Heatmap visual and relative-intensity reference: [Javis603/token-monitor](https://github.com/Javis603/token-monitor/tree/29ac4771f0a129899ea4ea1209a9bf23b0733bb8), MIT, Copyright 2026 Javis; see [licenses/MIT.txt](licenses/MIT.txt). Blue intensity steps, same-hue glow and the four positive bands relative to the visible period's peak are adapted to WinUI controls and composition; no JavaScript/CSS runtime is included. Reference paths: `src/electron/renderer/styles.css` and `src/electron/renderer/usageCharts.js`; the pinned source remains available at the upstream link; temporary inspection copies are not required for building or distribution.

## Weather artwork

Taskbar weather symbols (`Native/Builtins/Weather/res/weather_icons_*`) are QWeather artwork from [QWeather Icons](https://github.com/qwd/Icons) 1.8.0. QWeather and 和风天气 are credited for the icon artwork under CC BY 4.0; the attribution and full license are in [licenses/QWeather-CC-BY-4.0.txt](licenses/QWeather-CC-BY-4.0.txt). The project code is MIT, listed in [licenses/MIT.txt](licenses/MIT.txt). The quick-card symbols in `UI/Cards/WeatherIconArt.cs` are original two-tone vector geometry drawn for this product and carry no third-party artwork; the packaged build no longer contains the QWeather icon font.

Earlier [Meteocons](https://github.com/basmilius/meteocons) SVG source remains under `UI/Assets/WeatherIcons/` as a non-packaged design reference. Those unmodified `line`-style files retain the MIT license recorded in [licenses/MIT.txt](licenses/MIT.txt); no animated SMIL variants are distributed and the card artwork does not copy them.

## WinUI settings and cards

WinUI 3 settings and cards use the Microsoft.WindowsAppSDK.WinUI 2.3.9, InteractiveExperiences 2.1.9, Foundation 2.3.12, Base 2.0.4 and Runtime 2.5.1 components (NuGet), without the AI/ML/Search/Widgets metapackage dependencies. Their license terms and notices are consolidated in [licenses/Microsoft-WindowsAppSDK.txt](licenses/Microsoft-WindowsAppSDK.txt), and the transitive WebView2 terms in [licenses/Microsoft-WebView2.txt](licenses/Microsoft-WebView2.txt). The GUI is under `UI/`; lyrics/media services retain the source attribution above. Native VC143 runtime files retain Microsoft's redistributable terms. The lightweight package does not bundle a private shared .NET runtime.

## Brand artwork

Peripheral battery ROG / Republic of Gamers, Razer, Xiaomi and Apple marks (`Native/Cards/BrandLogos.h`) are unmodified SVG artwork from [Simple Icons 15.0.0](https://github.com/simple-icons/simple-icons/tree/15.0.0), under CC0 1.0 Universal. The Logitech G HUB mark is separately redrawn from the installed G HUB Windows icon resource and is **not CC0**. Source records, modification notes, the CC0 text and the proprietary Logitech notice are in [licenses/Brand-Artwork.txt](licenses/Brand-Artwork.txt). These marks identify devices and do not imply affiliation or endorsement. No runtime network assets or icon font are required.

## VigilWatt peripheral protocols

Peripheral acquisition reuses reviewed components from [evren-oct/vigil-watt](https://github.com/evren-oct/vigil-watt/tree/4df830043296760f5d5c273f4cdc0ea9456c4bc7), MIT, Copyright (c) 2026 evren. The license notice and pinned source are recorded in [licenses/MIT.txt](licenses/MIT.txt). Original snapshots, contributor acknowledgments and adaptation patches remain separately auditable in the source tree under `Dependencies/VigilWatt/`. The peripheral battery settings page links to the project in appreciation of its open-source contributors.

## AF-Media-Bar upstream snapshot notice

The upstream notice retained with the AF-Media-Bar snapshot additionally credits the TaskbarLyrics web lyrics presentation engine (Copyright (c) 2026 anync, MIT) for the files under `src/AFMediaBar/Web/Lyrics/`. That code is covered by [licenses/MIT.txt](licenses/MIT.txt).

The snapshot's NuGet package list, recorded for provenance rather than as shipped dependencies, was: CommunityToolkit.Mvvm 8.4.0 (MIT), Dubya.WindowsMediaController 2.5.6 (MIT), F23.StringSimilarity 7.0.1 (MIT), Lyricify.Lyrics.Helper 0.2.0 (Apache-2.0), MicaWPF 7.1.0 (MIT), Microsoft.Extensions.Hosting 10.0.1 (MIT), Microsoft.Web.WebView2 1.0.4191.47 (BSD-3-Clause), NAudio.Wasapi 3.1.0 (MIT), OpenccNetLib 1.7.0 (MIT), WPF-UI 4.2.0 (MIT) and WPF-UI.DependencyInjection 4.2.0 (MIT). Only the components integrated into this product are distributed; the current product inventory above governs the shipped package.