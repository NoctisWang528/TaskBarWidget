# Internal media protocol

Transport: local named pipe `TaskBarWidgets.<native PID>`, UTF-8 JSON objects separated by LF, version 1. C# helper is a current-user-only server, native host is client. Native host launches `TaskBarWidgets.Media.exe --host-pid <PID> --pipe-name TaskBarWidgets.<PID>` from its adjacent `Media/` directory. Helper exits when parent exits. Serialize writes, cap messages at 4 MiB, observe disconnects and cancellation. No remote clients.

Helper to native:

- `{ "version":1, "type":"initialize", "html":"..." }`: complete local lyrics HTML, styles and ordered scripts. Sent on every connection.
- `{ "version":1, "type":"frame", "sourceId":"...", "isPlaying":true, "hasMedia":true, "isBrowser":false, "trackId":"...", "scripts":["..."] }`: snapshot and trusted generated JS calls for existing lyrics renderer. Changes in source/effective playback state reset native manual override; trackId alone does not. Browser frames must not query lyrics.
- `{ "version":1, "type":"error", "message":"..." }`: bounded diagnostic; not arbitrary executable content.

Native to helper:

- `{ "version":1, "type":"command", "action":"togglePanel", "x":100, "y":100 }`: physical screen anchor at taskbar top edge; helper converts using target monitor DPI.
- Actions `openSettings`, `playPause`, `previous`, `next`, `shutdown` without extra fields.
- Action `seek`, `positionSeconds`: seek selected session if supported.
- Action `mediaVisible`, `visible`: whether native media renderer is visible; used to avoid hidden spectrum/render work.
- Action `openMonitorSettings`: native action originating from helper; reply may use the same command envelope helper-to-native. Native opens the unified settings window.
- Helper-to-native actions `showMedia` and `toggleMedia` support matching previews and the retained visibility hotkey; native owns the resulting manual display state.

Unified settings use helper-to-native commands `getNativeSettings` / `applyNativeSettings`, a unique string `requestId`, and `expiresAt` in Unix milliseconds. Apply carries a flat object of typed values in `payload`. The native host rejects expired or invalid requests before mutation and replies with action `nativeSettings`, the same requestId, `success`, `error`, and a schema/current-value payload: `{groups:[{id,label,page}],fields:[{key,label,group,type,value,options?,min?,max?}],configPath}`. `options` entries are `{value,label}`. Validation and persistence belong to native code. After partial failure the reply may include actual readback; the UI retains unconfirmed edits and displays the error instead of claiming success. Helper correlation expires after 12 seconds.

Built-in Battery/Power/Weather options appear as typed fields under hardware groups `builtins.battery`, `builtins.power`, `builtins.weather`. Historic DLL names only identify migrated settings; DLLs are never loaded. Unknown/credential fields remain local and are not serialized into the schema. `pluginOperation` and the plugins page have been removed. `openSettings` can select taskbar/hardware/audio/general/about.

All normal settings entry points open the unified helper window. Native owns shared appearance (`taskbar.fontName`, font attributes, `back_color`, `transparent`, `backgroundOpacity` 0–100). Native delivers `nativeAppearance` directly to the embedded renderer; later helper style frames retain these shared values. WebView2 uses a CompositionController; native HWND input routes mouse movement/clicks to it, while wheel and right click stay native. Windows 11 does not change layered-window styles at playback transitions.

Traffic history is requested with `getTrafficHistory`, `requestId`, and `expiresAt` using the same correlation/expiry rules. Response action is `trafficHistory`; success payload is `{today:"YYYY-MM-DD",days:[{date:"YYYY-MM-DD",totalKiB:number}],scopeLabel:string}`. Days cover Monday of the current week plus the previous eleven weeks through local today. Unknown and future days are absent; explicitly recorded zero days remain present. `totalKiB` is upload plus download, with legacy total-only records counted once and the live today snapshot replacing a same-date historical record. Native owns a bounded snapshot published by the monitor writer; UI requests do not scan files or contend with file I/O. The helper requests only while the panel is visible, on open and about every ten seconds, and cancels on hide or shutdown.

Frame queues retain complete snapshots. Before serialization the helper suppresses unchanged heavy scripts per connection; initialize/reconnect always includes the complete current style, cover and other state. A renderer reset must request a pipe reconnect so the complete state is resent. Commands use a separate reliable bounded queue.

Lyrics HTML includes a base URL of `https://taskbarwidgets.local/Web/Lyrics/`. The native host maps that virtual host to its local `Media/` directory for packaged fonts. No HTTP listener or remote page is involved.

Native owns the single taskbar area, automatic/manual display mode, wheel switching and right menu. Left click generally toggles panel, browser buttons send their action. WebView default context menu is disabled. Helper supplies a browser scene with metadata and controls, and forwards WebView actions through the native host to pipe.

Source layout: `Native/TrafficMonitor/` (copied C++ project), `Native/OpenHardwareMonitorApi/`, `Native/include/`, `Media/` (C# WPF helper), `Lyrics.Core/` (copied independent core). Runtime packaging: `artifacts/builds/<timestamp>/TaskBarWidgets.exe`, adjacent `Media/` and `UI/` subdirectories, and native hardware DLLs next to the main executable. Source code never references original project paths outside TaskBarWidgets.

Display-item field keys use stable IDs: `items.native_0.order` / `items.builtin_FxOP34Lm.order`. The itemId descriptor is also emitted. Ordering persists in `item_order_ids`, retaining numeric `item_order` only for compatibility. UI submits the whole permutation and preserves draft edits made during an in-flight save.


## WinUI frontend channel

The media helper launches sibling `UI/TaskBarWidgets.UI.exe --host-pid <media PID> --pipe-name TaskBarWidgets.UI.<media PID>`. It owns a second current-user-only newline UTF-8 pipe; WinUI is the client. Both ends cap messages at4MiB. Requests use `{version:1,type:"command",action,requestId,payload}` and responses `{version:1,type:"command",action:"uiResult",requestId,success,payload,error}`. UI events are `togglePanel` with top-level physical `x,y`, `openSettings` with optional page, and `shutdown`. Foreground permission is delegated from native to media and then to UI for explicit user interaction. The UI watches its host PID.

Actions: getState (media snapshot including source choices and local data-URL cover), getAudio (outputs/inputs, volumes0..1), mediaCommand, selectDevice, setVolume, setMute, getTrafficHistory, getNativeSettings/applyNativeSettings, getMediaSettings/applyMediaSettings and settingsCommand. Media settings retain existing names/validation and provider/hotkey/lyric rules; advanced service commands return `{settings,events:[{type,payload}]}`. No old settings window is shown.

Native module fields additionally expose `items.<stableId>.row` (0 or1). A changed module layout submits show/order/row for every module; horizontal_arrange switches one/two lanes without requiring different IDs. `item_rows` persists explicit lanes; numeric ordering remains only a migration compatibility field.
