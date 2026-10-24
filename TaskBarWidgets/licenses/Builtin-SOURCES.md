# Built-in monitoring sources

The native executable compiles these three providers directly. `ITMPlugin` is an internal adapter retained to preserve display-item IDs, layout ordering and rendering behavior; no plugin DLL is scanned or loaded.

| Feature | Pinned source | Local adaptation |
| --- | --- | --- |
| Battery | [zhongyang219/TrafficMonitorPlugins, eb7fc9e56f93fb69c99b185c6b8c395153d78bc6](https://github.com/zhongyang219/TrafficMonitorPlugins/tree/eb7fc9e56f93fb69c99b185c6b8c395153d78bc6/Plugins/Battery) | Keep `b5R30ITQ`; fixed legacy config filename; private type names and resource IDs; host-clock animation and GDI cleanup. |
| PowerMon | [AzulEterno/PowerMonPlugin-For-TrafficMonitor, b4440c089f25ea1be38843527152185f7831288e](https://github.com/AzulEterno/PowerMonPlugin-For-TrafficMonitor/tree/b4440c089f25ea1be38843527152185f7831288e/PowerMonPlugin) | Keep all eight IDs and battery-driver/formatting source; CPU/GPU/smart items share the product hardware monitor instead of initializing another CLR computer. |
| WeatherPro | [Haojia521/TrafficMonitorPlugins, weather-pro-v0.10, 49c68beb43648d599b593114b519269e40be411e](https://github.com/Haojia521/TrafficMonitorPlugins/tree/49c68beb43648d599b593114b519269e40be411e/WeatherPro) | Match the installed 0.10 config and `FxOP34Lm` ID. The [successor repository](https://github.com/Haojia521/TMP-WeatherPro) changes config semantics, so this migration pins the compatible source. WinHTTP supplies bounded requests, response size and gzip decoding; one owned worker replaces detached threads. |

Only display resources are embedded. Plugin options windows and update/download controls are omitted; allowlisted options are edited through unified Hardware settings. Active configuration lives in `Data/config/battery.ini`, `power.ini` and `weather.ini`; legacy `plugins/Battery.dll.ini`, `PowerMonPlugin.dll.ini` and `WeatherPro.ini` names remain migration inputs only. Weather credentials never enter IPC and are stored as current-user DPAPI ciphertext outside the INI.

Battery and PowerMon retain their MIT upstream notices; WeatherPro retains the upstream license copied with its snapshot. `Weather/SimpleIni.h` carries its MIT notice; `utilities/yyjson/yyjson.[ch]` carries its MIT notice. See the packaged `licenses/` directory and `THIRD-PARTY-NOTICES.md`.
