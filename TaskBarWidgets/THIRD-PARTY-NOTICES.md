# Source attribution

TaskBarWidgets integrates source from the reference snapshot in workspace commit `a0783de5a245d11d830882ccb5b79964cf1d53af`. Modifications belong to this product directory; the reference trees remain unchanged. Original author names and license notices are preserved. This document does not relicense third-party code.

| Product content | Source | Notice |
| --- | --- | --- |
| Native taskbar, monitoring, plugin API and hardware wrapper | zhongyang219/TrafficMonitor, workspace `TrafficMonitor/` | [Anti 996 license](licenses/TrafficMonitor-LICENSE), [Chinese copy](licenses/TrafficMonitor-LICENSE_CN) |
| Lyrics core, media session services, Web rendering, settings and associated tests | ANYNC/TaskbarLyrics, workspace `TaskbarLyrics/` | [MIT license](licenses/TaskbarLyrics-LICENSE) |
| Audio endpoint policy switching and related behavior | Fervent-Tempo/AF-Media-Bar, workspace `AF-Media-Bar/` | [MIT license](licenses/AF-Media-Bar-LICENSE); [upstream third-party notices](licenses/AF-Media-Bar-THIRD-PARTY-NOTICES.md) |

Native runtime dependencies include LibreHardwareMonitor and the copied OpenHardwareMonitorApi wrapper. The unchanged `LibreHardwareMonitorLib.dll` reports version `0.9.4+b8077435b898d57539956388cddf49f7dacb86f7`; its corresponding source is available at the [pinned upstream commit](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/b8077435b898d57539956388cddf49f7dacb86f7), under the [Mozilla Public License 2.0](licenses/LibreHardwareMonitor-LICENSE). Its embedded WinRing0 and InpOut components retain their [WinRing0 notice](licenses/WinRing0-LICENSE.txt), [InpOut notice](licenses/InpOut-LICENSE.txt), and the [complete upstream license page](licenses/LibreHardwareMonitor-upstream-notices.html) from that same commit. Earlier versions bundled Source Han Sans; current builds use system fonts and retain the historical [SIL Open Font License](licenses/SourceHanSans-LICENSE.txt), obtained from [Adobe's source repository](https://github.com/adobe-fonts/source-han-sans). Packaging includes this file and the `licenses/` directory.

Battery, PowerMon and WeatherPro are compiled directly from pinned upstream source into the native executable; no personal plugin DLLs are distributed or dynamically scanned. Their migrated configuration stays local. Source versions and adaptations are recorded in [licenses/Builtin-SOURCES.md](licenses/Builtin-SOURCES.md).

| Built-in feature | Source | Notice |
| --- | --- | --- |
| Battery display | [zhongyang219/TrafficMonitorPlugins](https://github.com/zhongyang219/TrafficMonitorPlugins), `eb7fc9e56f93fb69c99b185c6b8c395153d78bc6` | [MIT](licenses/Battery-LICENSE) |
| Battery and hardware power | [AzulEterno/PowerMonPlugin-For-TrafficMonitor](https://github.com/AzulEterno/PowerMonPlugin-For-TrafficMonitor), `b4440c089f25ea1be38843527152185f7831288e` | [MIT](licenses/PowerMon-LICENSE) |
| WeatherPro compatible with migrated 0.10 configuration | [Haojia521/TrafficMonitorPlugins](https://github.com/Haojia521/TrafficMonitorPlugins), tag `weather-pro-v0.10`, `49c68beb43648d599b593114b519269e40be411e` | [Upstream license](licenses/WeatherPro-LICENSE) |

WeatherPro's bundled `SimpleIni.h` and `yyjson` parser preserve their embedded MIT notices. The current WeatherPro successor is [Haojia521/TMP-WeatherPro](https://github.com/Haojia521/TMP-WeatherPro).

Binary distributions also include the [SimpleIni MIT text](licenses/SimpleIni-LICENSE.txt), [yyjson 0.4.0 MIT text](licenses/yyjson-LICENSE.txt), [original yyjson header notice](licenses/yyjson-HEADER-NOTICE.txt), and [tinyxml2 source notice](licenses/tinyxml2-LICENSE.txt).

Managed and Windows App SDK runtime/native dependency authors, resolved versions, license declarations, source locations and copied license files are listed in [the dependency inventory](licenses/Managed-DEPENDENCIES.md). This is the current product's inventory; the preserved AF-Media-Bar notice above describes its original upstream snapshot. WebView2 license/notice texts for both resolved versions and historical .NET runtime license/notices accompany that inventory. Current lightweight packages use system-installed .NET and Windows App Runtime; application-local managed projections and bootstrap libraries retain their notices. [License text sources and SHA-256 hashes](licenses/License-SOURCES.json) record unmodified copies. Visual C++/MFC runtime files retain Microsoft's redistributable terms.

The lyrics helper is built from a pinned source fork: [Lyricify.Lyrics.Helper Apache-2.0 license](licenses/Lyricify-Lyrics-Helper-LICENSE), [source, modification and parser notices](licenses/Lyricify-Lyrics-Helper-NOTICE). The source fork uses OpenCC and does not distribute CHTCHSConv/ChineseConverter.


Heatmap visual and relative-intensity reference: [Javis603/token-monitor](https://github.com/Javis603/token-monitor/tree/29ac4771f0a129899ea4ea1209a9bf23b0733bb8), [MIT](licenses/token-monitor-LICENSE) (Copyright 2026 Javis). Blue intensity steps, same-hue glow and the four positive bands relative to the visible period's peak are adapted to WinUI controls and composition; no JavaScript/CSS runtime is included. Reference paths: `src/electron/renderer/styles.css` and `src/electron/renderer/usageCharts.js`; the pinned source remains available at the upstream link; temporary inspection copies are not required for building or distribution.

Current weather card symbols are vector paths derived from the unmodified `qweather-icons.ttf` outlines from [QWeather Icons](https://github.com/qwd/Icons) 1.8.0. QWeather and 和风天气 are credited for the icon artwork under [CC BY 4.0](licenses/QWeather-Icons-CC-BY-4.0-LICENSE.txt); the project code is under the [MIT license](licenses/QWeather-Icons-LICENSE.txt). The paths preserve the icon artwork without redrawing or restyling it.

Earlier [Meteocons](https://github.com/basmilius/meteocons) SVG source remains under `UI/Assets/WeatherIcons/` as a non-packaged design reference. Those unmodified `line`-style files retain the [MIT license](licenses/Meteocons-LICENSE.txt); no animated SMIL variants are distributed.


WinUI 3 settings and cards use the Microsoft.WindowsAppSDK.WinUI 2.3.9 and InteractiveExperiences 2.1.9 components (NuGet), without the AI/ML/Search/Widgets metapackage dependencies, with its [license](licenses/WindowsAppSDK-LICENSE.txt) and [third-party notices](licenses/WindowsAppSDK-NOTICE.txt) packaged alongside the product. The GUI is under `UI/`; lyrics/media services retain the source attribution above. Native VC143 and the privately bundled shared .NET runtime retain their respective Microsoft terms. Runtime/LICENSE.txt and Runtime/ThirdPartyNotices.txt accompany the private host; framework versions and file hashes are recorded in Runtime/runtime-manifest.json. Removed WPF-UI and font license texts remain for provenance.
