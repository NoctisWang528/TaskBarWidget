# Internal media protocol

Transport: local named pipe `TaskBarWidgets.<native PID>`, UTF-8 JSON objects separated by LF, version 1. C# helper is a current-user-only server, native host is client. Native host launches `TaskBarWidgets.Media.exe --host-pid <PID> --pipe-name TaskBarWidgets.<PID>` from its adjacent `Media/` directory. Helper exits when parent exits. Serialize writes, cap messages at 4 MiB, observe disconnects and cancellation. No remote clients.

Helper to native:

- `{ "version":1, "type":"initialize", "html":"..." }`: complete local lyrics HTML, styles and ordered scripts. Sent on every connection.
- `{ "version":1, "type":"frame", "sourceId":"...", "isPlaying":true, "hasMedia":true, "isBrowser":false, "trackId":"...", "scripts":["..."] }`: snapshot and trusted generated JS calls for existing lyrics renderer. Changes in source/effective playback state reset native manual override; trackId alone does not. Browser frames must not query lyrics.
- `{ "version":1, "type":"error", "message":"..." }`: bounded diagnostic; not arbitrary executable content.

Native to helper:

- `{ "version":1, "type":"command", "action":"togglePanel", "x":100, "y":100, "entryWindow":1234 }`: physical screen anchor at taskbar top edge and native entry HWND. The media helper verifies the HWND belongs to its native host PID before forwarding; missing/invalid handles become zero. UI uses this only to let the original left-click toggle handle entry clicks once, rather than also dismissing them as outside clicks.
- Actions `openSettings`, `playPause`, `previous`, `next`, `shutdown` without extra fields.
- Action `seek`, `positionSeconds`: seek selected session if supported.
- Action `mediaVisible`, `visible`: whether native media renderer is visible; used to avoid hidden spectrum/render work.
- Action `openMonitorSettings`: native action originating from helper; reply may use the same command envelope helper-to-native. Native opens the unified settings window.
- Helper-to-native actions `showMedia` and `toggleMedia` support matching previews and the retained visibility hotkey; native owns the resulting manual display state.

Unified settings use helper-to-native commands `getNativeSettings` / `applyNativeSettings`, a unique string `requestId`, and `expiresAt` in Unix milliseconds. Apply carries a flat object of typed values in `payload`. The native host rejects expired or invalid requests before mutation and replies with action `nativeSettings`, the same requestId, `success`, `error`, and a schema/current-value payload: `{groups:[{id,label,page}],fields:[{key,label,group,type,value,options?,min?,max?}],configPath}`. `options` entries are `{value,label}`. Validation and persistence belong to native code. After partial failure the reply may include actual readback; the UI retains unconfirmed edits and displays the error instead of claiming success. Helper correlation expires after 12 seconds.

Built-in Battery/Power/Weather options appear as typed fields under hardware groups `builtins.battery`, `builtins.power`, `builtins.weather`. Historic DLL names only identify migrated settings; DLLs are never loaded. Unknown/credential fields are not serialized into the settings schema. Weather keys have a separate explicit write-only configuration path described below; stored keys are never returned. `pluginOperation` and the plugins page have been removed. `openSettings` can select taskbar/hardware/audio/general/about.

All normal settings entry points open the unified helper window. Native owns shared appearance (`taskbar.fontName`, font attributes, `back_color`, `transparent`, `backgroundOpacity` 0–100). Native delivers `nativeAppearance` directly to the embedded renderer; later helper style frames retain these shared values. WebView2 uses a CompositionController; native HWND input routes mouse movement/clicks to it, while wheel and right click stay native. Windows 11 does not change layered-window styles at playback transitions.

Traffic history is requested with `getTrafficHistory`, `requestId`, and `expiresAt` using the same correlation/expiry rules. Response action is `trafficHistory`; success payload is `{today:"YYYY-MM-DD",days:[{date:"YYYY-MM-DD",totalKiB:number}],scopeLabel:string}`. Days cover Monday of the current week plus the previous eleven weeks through local today. Unknown and future days are absent; explicitly recorded zero days remain present. `totalKiB` is upload plus download, with legacy total-only records counted once and the live today snapshot replacing a same-date historical record. Native owns a bounded snapshot published by the monitor writer; UI requests do not scan files or contend with file I/O. The helper requests only while the panel is visible, on open and about every ten seconds, and cancels on hide or shutdown.

Frame queues retain complete snapshots. Before serialization the helper suppresses unchanged heavy scripts per connection; initialize/reconnect always includes the complete current style, cover and other state. A renderer reset must request a pipe reconnect so the complete state is resent. Commands use a separate reliable bounded queue.

Lyrics HTML includes a base URL of `https://taskbarwidgets.local/Web/Lyrics/`. The native host maps that virtual host to its local `Media/` directory for packaged fonts. No HTTP listener or remote page is involved.

Native owns the single taskbar area, automatic/manual display mode, wheel switching and right menu. Left click generally toggles panel, browser buttons send their action. WebView default context menu is disabled. Helper supplies a browser scene with metadata and controls, and forwards WebView actions through the native host to pipe.

Source layout: `Native/TrafficMonitor/` (copied C++ project), `Native/OpenHardwareMonitorApi/`, `Native/include/`, `Media/` (C# WPF helper), `Lyrics.Core/` (copied independent core). Runtime packaging: `artifacts/builds/<timestamp>/TaskBarWidgets.exe`, adjacent `Media/` and `UI/` subdirectories, and native hardware DLLs next to the main executable. Source code never references original project paths outside TaskBarWidgets.

Display-item field keys use stable IDs: `items.native_0.order` / `items.builtin_FxOP34Lm.order`. The itemId descriptor is also emitted. Ordering persists in `item_order_ids`, retaining numeric `item_order` only for compatibility. UI submits the whole permutation and preserves draft edits made during an in-flight save.


## WinUI frontend channel

The media helper launches sibling `UI/TaskBarWidgets.UI.exe --host-pid <media PID> --pipe-name TaskBarWidgets.UI.<media PID>`. It owns a second current-user-only newline UTF-8 pipe; WinUI is the client. Both ends cap messages at4MiB. Requests use `{version:1,type:"command",action,requestId,payload}` and responses `{version:1,type:"command",action:"uiResult",requestId,success,payload,error}`. UI events are `togglePanel` with top-level physical `x,y`, `openSettings` with optional page, and `shutdown`. Foreground permission is delegated from native to media and then to UI for explicit user interaction. The UI watches its host PID.

Actions: getState (media snapshot including source choices and local data-URL cover), getAudio (outputs/inputs, volumes0..1), mediaCommand, selectDevice, setVolume, setMute, getTrafficHistory, getNativeSettings/applyNativeSettings, getMediaSettings/applyMediaSettings, getFonts and settingsCommand. getFonts returns installed font choices as `[{value,label}]`; the native font-name length limit still applies. Media settings retain existing names/validation and provider/hotkey/lyric rules; advanced service commands return `{settings,events:[{type,payload}]}`. No old settings window is shown.

Native module fields additionally expose `items.<stableId>.row` (0 or1). A changed module layout submits show/order/row for every module; horizontal_arrange switches one/two lanes without requiring different IDs. `item_rows` persists explicit lanes; numeric ordering remains only a migration compatibility field.

The UI bridge admits requests into two bounded FIFO lanes (16 waiting entries each): audio (`getAudio`, `selectDevice`, `setVolume`, `setMute`) and other requests. Reading the pipe does not await request execution. Audio operations and replies retain their order; shutdown cancels queued work. A full lane returns a correlated error instead of allocating unbounded jobs.

Audio operations return `{outputs,inputs,operationSucceeded,message}` directly. Endpoint entries retain existing fields and add optional `canControl` (default true for older snapshots); false means volume/mute could not be read, so selectors remain usable but volume/mute controls are disabled. Partial default-role changes return actual readback with `operationSucceeded:false`. Mute changes only the mute property. UI request sequence and mutation generation prevent stale polls/replies from replacing new state. `togglePanel` also forwards the validated numeric `entryWindow` described above; focus changes alone never dismiss cards.

## Weather settings

UI `weatherCommand` is forwarded to the native command of the same name, using existing request IDs/expiry. Native replies as `weatherResult`. Payload `operation` is `status`, `search`, `configure`, `refresh` or `result`. Provider 0 is China Weather; 1 is QWeather. Search takes `{provider,query}` and returns provider-specific `{id,name,region}` candidates. City selection is explicit; merely typing a display name cannot silently change the committed city ID.

Search/configure/refresh return `{jobId,pending,success,error,cities,status}`; UI polls `result` with a numeric job ID. Networking is serialized on the weather worker, while status/results return immediately on the native UI thread. Queue and retained job results are bounded. Late or cancelled search results cannot replace the current provider/search.

`configure` with `{provider,city:{id,name,region}}` saves and refreshes. `{credentialsOnly:true,apiHost,apiKey?,clearApiKey}` saves the QWeather connection without changing the current city/provider. Blank/omitted keys preserve the stored key; clearing requires the explicit flag. Keys travel only in that current-user local request and are never returned or logged. The existing local WeatherPro INI remains the storage location and must never be included in releases. QWeather credentials are sent only via HTTPS `X-QW-Api-Key` to a validated dedicated `*.qweatherapi.com` host, with redirects disabled.

Status includes selected provider/city, normalized API host, `hasApiKey`, update state, safe message, temperature, and attempt/success times. It never contains raw provider responses or secret values. Failures must remain visible; successful snapshots survive transient failures for the same location. Generic weather display preferences trigger a queued reload/refresh after saving rather than requiring an app restart.
