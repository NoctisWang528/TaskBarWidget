# Source attribution

TaskBarWidgets integrates source from the reference snapshot in workspace commit `a0783de5a245d11d830882ccb5b79964cf1d53af`. Modifications belong to this product directory; the reference trees remain unchanged. Original author names and license notices are preserved. This document does not relicense third-party code.

| Product content | Source | Notice |
| --- | --- | --- |
| Native taskbar, monitoring, plugin API and hardware wrapper | zhongyang219/TrafficMonitor, workspace `TrafficMonitor/` | [Anti 996 license](licenses/TrafficMonitor-LICENSE), [Chinese copy](licenses/TrafficMonitor-LICENSE_CN) |
| Lyrics core, media session services, Web rendering, settings and associated tests | ANYNC/TaskbarLyrics, workspace `TaskbarLyrics/` | [MIT license](licenses/TaskbarLyrics-LICENSE) |
| Audio endpoint policy switching and related behavior | Fervent-Tempo/AF-Media-Bar, workspace `AF-Media-Bar/` | [MIT license](licenses/AF-Media-Bar-LICENSE); [upstream third-party notices](licenses/AF-Media-Bar-THIRD-PARTY-NOTICES.md) |

Native runtime dependencies include LibreHardwareMonitor and the copied OpenHardwareMonitorApi wrapper. [LibreHardwareMonitor license](licenses/LibreHardwareMonitor-LICENSE) is copied from its [upstream repository](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor); source for the bundled upstream library remains available there. Managed package dependencies retain their NuGet package licenses; package metadata is restored into `.packages/`. WebView2 and the Visual C++/.NET runtimes retain Microsoft's respective terms. Source Han Sans font assets retain their [SIL Open Font License](licenses/SourceHanSans-LICENSE.txt), obtained from [Adobe's source repository](https://github.com/adobe-fonts/source-han-sans). Packaging must include this file and the `licenses/` directory.

Battery, PowerMon and WeatherPro are compiled directly from pinned upstream source into the native executable; no personal plugin DLLs are distributed or dynamically scanned. Their migrated configuration stays local. Source versions and adaptations are recorded in [licenses/Builtin-SOURCES.md](licenses/Builtin-SOURCES.md).

| Built-in feature | Source | Notice |
| --- | --- | --- |
| Battery display | [zhongyang219/TrafficMonitorPlugins](https://github.com/zhongyang219/TrafficMonitorPlugins), `eb7fc9e56f93fb69c99b185c6b8c395153d78bc6` | [MIT](licenses/Battery-LICENSE) |
| Battery and hardware power | [AzulEterno/PowerMonPlugin-For-TrafficMonitor](https://github.com/AzulEterno/PowerMonPlugin-For-TrafficMonitor), `b4440c089f25ea1be38843527152185f7831288e` | [MIT](licenses/PowerMon-LICENSE) |
| WeatherPro compatible with migrated 0.10 configuration | [Haojia521/TrafficMonitorPlugins](https://github.com/Haojia521/TrafficMonitorPlugins), tag `weather-pro-v0.10`, `49c68beb43648d599b593114b519269e40be411e` | [Upstream license](licenses/WeatherPro-LICENSE) |

WeatherPro's bundled `SimpleIni.h` and `yyjson` parser preserve their embedded MIT notices. The current WeatherPro successor is [Haojia521/TMP-WeatherPro](https://github.com/Haojia521/TMP-WeatherPro).


Heatmap glow visual reference: [Javis603/token-monitor](https://github.com/Javis603/token-monitor/tree/29ac4771f0a129899ea4ea1209a9bf23b0733bb8), MIT (Copyright 2026 Javis). The same-hue glow is newly implemented with WPF effects; no JavaScript/CSS implementation was copied. Reference paths: `src/electron/renderer/styles.css`, heatmap glow rendering in `usageCharts.js`; exact inspected files are retained in local `artifacts/references/token-monitor/`.


WinUI 3 settings and cards use Microsoft.WindowsAppSDK 2.5.1 (NuGet), with its [license](licenses/WindowsAppSDK-LICENSE.txt) and [third-party notices](licenses/WindowsAppSDK-NOTICE.txt) packaged alongside the product. The GUI is under `UI/`; lyrics/media services retain the source attribution above. Native VC143 and self-contained .NET runtime terms remain their respective Microsoft terms.
