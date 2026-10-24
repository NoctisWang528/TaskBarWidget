# 验收状态

更新：2026-10-24。最近成功完整构建的路径由本机 `artifacts/latest-build.txt` 提供；构建产物、布局检查图、日志和用户配置不提交到仓库。

## 撤销 API Host 硬编码误拦截（2026-10-24）

用户确认使用账户自己的 API Host 后，发现 `261024-1750` 会在真实请求发出前将一个固定域名判定为文档示例。这是本地硬编码判断错误，不能根据域名与文档内容相同推断账户归属。现已移除该判断，仅保留 HTTPS、域名格式及 qweatherapi.com 边界校验；连接与鉴权以真实服务响应为准。设置说明、协议和合成回归同步更正，不再把硬编码拒绝当作正确行为。

用户已明确要求不要启动软件或测试探针。本次只进行静态检查、编译和包内容校验，不运行产品、原生测试程序或离屏探针，不更改个人配置。用户随后授权撤回 `261024-1750` 并重新发布修正版；旧 Release 转为草稿停止公开分发，保留原标签和提交历史。下文 `261024-1750` 的示例 Host 拦截描述为历史错误行为，已由本节修正，不能作为 Host 无效的证据。

编译结果：完整 Release `261024-1816`（`artifacts/builds/20261024-181609`）通过；修改后的原生回归源码仅编译为对象文件，未链接或启动测试程序。回归已改为允许格式合法的不同自定义 Host 通过合成请求完成搜索、刷新与保存，但本次未运行回归或实际接口，不能宣称网络功能已验收。

重新发行：修正提交 `b5c1bd1` 由合并提交 `7261ccf` 合入主分支，完整硬件 Release `261024-1820`（`artifacts/builds/20261024-182050`）编译成功。根据用户明确要求，本轮没有执行产品、测试程序或探针；分发前只检查清单、哈希、包内容以及受信任托管校验器的解包逻辑，上传草稿后下载附件复核，再发布新版。旧版 `261024-1750` 保留为草稿，标签和提交历史不删除；新版说明延续上一版用户可见变更，移除失效的示例地址拦截描述，不新增本次错误修复条目。

## 和风修复合入与发行构建（2026-10-24）

`codex/qweather-transport-aqi` 的连接诊断与空气质量改动已由提交 `18e1324` 合入主分支，合并提交为 `7aa705e`。主分支完整硬件 Release 为 `261024-1750`（`artifacts/builds/20261024-175033`）。天气服务回归、真实 WinHTTP 回环、原生状态／布局／管道回归、内置模块启动、最终包 WinUI 离屏与组件、共享运行库及缺失／错误版本诊断均通过；离屏窗口未激活。

发行采用干净提交来源，打包和草稿附件下载后均使用已发布旧版 `261023-1616` 的完整包校验器检查更新兼容性，并使用隔离更新器夹具检查完整清单预检；相关报告保存在本机 `artifacts/package-checks/`、`artifacts/updater-tests/` 和发行附件下载目录中。未使用个人 API Key 请求真实服务，未启动或替换日常安装，实际账户权限与地理覆盖、桌面指针交互和混合 DPI 的限制沿用下节说明。

## 和风连接诊断与空气质量 v1（2026-10-24）

连接验证、搜索与刷新统一拒绝官方文档中的完整示例 Host，提示从账户控制台复制专属地址；不按后缀推测或修改用户配置。连接失败在实际 WinHTTP 调用处保留错误及阶段，非成功 HTTP 响应仅在固定长度内读取，并按已知和风错误类型映射安全提示。共享连接、鉴权与账户错误终止本轮后续请求；不把 HTTP 200 后的断流当作完整成功，也不显示上游任意标题、正文或凭据。

空气质量迁移至 `/airquality/v1/current/{latitude}/{longitude}`，与预警每轮共享一次坐标解析；显式经纬度直接使用，GeoAPI 不跨轮缓存。优先中国标准 `cn-mee`，其次其他本地标准，最后 QAQI；展示 `aqiDisplay` 与实际标准，污染物浓度保留有效零值和单位，缺失不补零，不编造观测时间。空气质量失败只保留同地点上次数据并显示独立警告，切换地点或关闭时清空，归因与预警归因合并去重显示。

验证：完整硬件 Release `261024-1745`（`artifacts/builds/20261024-174502`）、`Native/tests/run-weather-service-tests.ps1`、`Native/tests/run-weather-http-tests.ps1` 和最终包 `scripts/validate-winui.ps1` 均通过。天气服务回归包括示例地址不联网、错误正文不透出密钥、共享故障短路、实况成功后预报阶段共享故障仍报告失败、v1 空气质量映射及恢复。回环测试不替换传输函数，实际验证 Gzip、非成功 HTTP、跳转禁用、断流、超时与响应上限，不涉及真实账户或系统代理/证书变更。WinUI 合成探针验证标准、单位、零值、归因去重、失败警告和连接输入保留；深浅主题截图已检查。完整构建仍有未改动模块的既有警告。

限制：回环证明能够识别超时和限制响应体读取总时长，不代表严格三秒端到端超时保证；Windows WinHTTP 的重试及时序存在变化。离屏窗口无法把投递的鼠标消息提升成真实指针事件，因此没有实际拖动验收。Claude Code 独立复核触及预设预算后停止，未形成最终结论，不作为通过证据；编译、回归和差异检查由主代理完成。

实际接口验收仍需用户在设置里填写账户控制台分配的专属 Host，再验证连接、搜索江宁并刷新。合成及回环测试不证明真实账户权限、额度或预警地理覆盖。用户已授权将本次改动合入主分支、重新完整构建、提交、推送并发布干净 Release；不启动正常产品，不替换日常安装。正式发行先上传草稿附件，再用下载后的附件与旧版完整校验器检查通过后发布。

## 天气与单曲偏移修复合并发布（2026-10-23）

`codex/weather-alert-flow` 和 `codex/fix-offset-page-refresh` 已合入主分支。单曲偏移页面收到数据后仅更新当前歌曲与历史列表，保留分组、搜索控件和未提交输入，不再重建整页或重复播放入场动画。

合并后的完整硬件 Release 为 `261023-1616`（`artifacts/builds/20261023-161623`）。天气原生回归、原生状态／布局／管道／离屏渲染回归、内置模块启动、最终包 WinUI 离屏模板与组件、共享运行库及缺失／错误版本诊断均通过。单曲偏移探针确认初次仅构建一次页面，回包不重建，搜索与当前歌曲草稿保留，过期响应被忽略；天气探针覆盖预警失败提示、归因与来源草稿。深浅主题天气卡离屏图片已检查。

本机 .NET 通用测试宿主在监听父进程退出时抛出 `Win32Exception (5): Access denied`；歌词与媒体测试分别重试后仍在执行测试前中止，因此本轮不能宣称这两套 .NET 单元测试通过。相关日志保留在 `artifacts/logs/`。上述原生回归和 WinUI 组件探针正常执行并通过。

本轮没有启动正常产品、替换日常安装或使用个人密钥请求真实接口。实际桌面观感、真实 API 权限与地理覆盖、Explorer 和混合 DPI 验收仍待确认。

## 和风天气预警与来源切换流程（2026-10-23）

和风预警改用 `/weatheralert/v1/current/{latitude}/{longitude}`：Location ID 通过 GeoAPI 获取坐标，经纬度位置直接沿用输入坐标，统一按接口要求保留两位小数。新响应校验 `metadata.zeroResult` 和 `alerts`，映射事件、颜色级别、正文与时间，排除撤销通知，并显示返回的归因信息。

两种天气来源都通过独立的 `details.alertsError` 报告预警失败；天气主体仍可正常刷新，但设置提示使用警告状态，卡片显示失败原因。仅同一城市保留上次预警并标明可能过时，成功空结果清除旧预警和错误。中国天气网缺少预警数组的响应不再被当成成功空结果。

和风连接保存前通过 GeoAPI 验证候选 Host/Key，失败不覆盖连接配置。设置区分当前生效来源和草稿，提供明确的“切换来源并刷新”；来源、城市或可见的连接草稿未提交时禁止刷新旧选择。刷新成功提示包含实际来源和城市，连接验证成功不表示来源已切换。清除密钥不验证连接，也不提交或丢弃未保存的 Host 草稿。

验证：完整硬件 Release `261023-1609`（`artifacts/builds/20261023-160940`）、`Native/tests/run-weather-service-tests.ps1` 和最终包 `scripts/validate-winui.ps1` 均通过。原生合成响应覆盖新接口、取消通知、坐标顺序、错误保留/恢复、空结果与无效坐标；WinUI 合成探针覆盖来源草稿、切换、连接验证失败、未保存连接与独立预警错误提示。深浅主题天气卡和空状态的离屏图片已检查。完整构建仍包含未改动模块的既有编译警告。

限制：没有使用个人 API Key 请求真实天气，没有启动正常产品或替换日常安装；真实接口权限、江宁/海区预警的地理覆盖、实际桌面交互和混合 DPI 验收仍待进行。用户于本日授权将天气与单曲偏移修复合入主分支，构建、提交、推送、发布干净 ZIP，并清理对应工作树。

## 任务栏浏览器媒体两行文字底部截断修复（2026-10-23）

用户截图里任务栏浏览器媒体的标题与歌手／来源行字底被削平。原因是 `PaintBrowser` 把字号当成了裁剪高度：DirectWrite 的行框比字号高约三分之一，`DrawBrowserText` 的裁剪底边落在字号处，正好切掉每行文字的下沿；第二行又按标题字号而不是真实行高堆叠，两行在竖直方向上互相压住。

- [NativeMediaPainter.h:238-242](Native/TrafficMonitor/NativeMediaPainter.h) 把原本的 `height` 参数改名为 `lineHeight`，并新增 `MeasureBrowserLine`：取受限文本布局的 `DWRITE_TEXT_METRICS.height`，取不到时退回自然行高，且不小于字号。这与封面占位字母按行高居中的既有做法（[NativeMediaPainter.cpp:680-684](Native/TrafficMonitor/NativeMediaPainter.cpp)）一致。
- [NativeMediaPainter.cpp:760-779](Native/TrafficMonitor/NativeMediaPainter.cpp) 先量出标题行与第二行的行框高度，再按两者之和在按钮区左侧的 metadata 矩形里垂直居中；第二行从 `titleTop + titleHeight` 起画，不再按字号堆叠。字号、字重、颜色、省略号截断和按钮命中区域都没有变化。
- [NativeMediaPainter.cpp:729-745](Native/TrafficMonitor/NativeMediaPainter.cpp) 的裁剪矩形改为行框高度，绘制仍在这一处轴对齐裁剪内完成。

回归：[NativeMediaOffscreenTests.cpp:151-171](Native/tests/NativeMediaOffscreenTests.cpp) 新增按亮度找最低墨迹行的助手（背景层不透明，靠 alpha 判断没有意义）；[NativeMediaOffscreenTests.cpp:476-494](Native/tests/NativeMediaOffscreenTests.cpp) 用 `Fixture artist` 与带下伸字母的 `Fixture artist gyp` 各渲染一次，要求后者的墨迹底行更低。把裁剪高度改回字号做反向验证时该断言失败（`Browser metadata descenders are not clipped.`），修复后通过。

验证：完整硬件 Release 构建 `artifacts/builds/20261023-152142`（版本 `261023-1521`）通过；`Native/tests/run-native-tests.ps1` 全部通过，含本次新增的离屏断言（离屏图 `Native\Bin\Tests\Offscreen\browser-controls.png` 与 `browser-metadata.png`）；`tests/Media.Tests` 311/311 通过。

未验证：真机任务栏上的浏览器媒体、混合 DPI、不同字号与系统字体回退时的两行排版和按钮对齐——按项目规则没有启动日常安装或正常产品。`scripts/verify.ps1` 在本机 `tests/Lyrics.Core.Tests` 有 9 个与本改动无关的既有失败（`LyricPipelineCacheTests` 在数据根未配置时触发静态构造异常，`DataLayoutTests.MigrationRejectsInvalidJsonWithoutCommitting` 期望 `JsonException` 精确类型而实际抛出派生类型 `JsonReaderException`）；原生与 Media 测试均通过。

## 天气卡空状态刷新入口（2026-10-23）

天气来源没有返回完整数据时，卡片正文会整体换成空状态；此前空状态只有一段错误说明，用户没有任何重试动作。现在空状态在说明文字下始终提供与左栏相同的“刷新天气”按钮；没有城市时继续保留原有的“打开天气设置”，因为它仍是那时唯一有用的入口。

- [UI/Cards/WeatherCard.cs:522-536](UI/Cards/WeatherCard.cs) 新增两个助手：`SetWeatherRefreshEnabled(bool enabled)` 同时驱动左栏与空状态两个刷新按钮，`SetWeatherStatus(string text)` 在空状态可见时把进度与失败同步写进空状态文案，避免在空状态点刷新后看不见任何反馈；空状态是否显示设置入口沿用原有 `hasCity` 判断（`:353`）。
- 同一文件 `:241-242` 把两个入口放进左对齐的水平 `StackPanel`（间距 8），`:247-281` 的刷新流程与 `:286-310` 的读取流程改走上述助手；刷新失败只更新文案，不清空已经渲染的数据。
- 探针回归 [UI/Cards/WeatherCardProbe.cs:196-230](UI/Cards/WeatherCardProbe.cs) 用 `revision = 48` 的不完整快照（`state = "error"`、文案“中国天气网未返回完整的天气数据。”、城市南京、`details.current` 为空）断言空状态可见、文案来自提供方、刷新按钮可见且启用、设置入口折叠，并用 `TransformToVisual` 断言按钮实际尺寸大于零且不越出卡片边界（离屏图片 `ui-weather-check/weather-card-empty.png` 另行人工核对）；随后通过 `ButtonAutomationPeer` + `IInvokeProvider` 真实点击，断言空状态出现“天气刷新失败：”且按钮重新启用。探针没有媒体后端，因此这条路径必然以失败收尾，正是需要验证的分支。
- [scripts/test-ui-templates.ps1:93-99](scripts/test-ui-templates.ps1) 把 `emptyStateRefresh` 加进 `weatherCardControls` 的必过条件；[docs/界面统一方案.md:53](docs/界面统一方案.md) 补充空状态行为描述。

验证：完整硬件 Release 构建 `artifacts/builds/20261023-140023`（版本 `261023-1400`）通过，边界检查 289 个字面输入，原生、Media、UI 三处构建均为 0 warning／0 error，共享 .NET／Windows App Runtime 布局检查通过。对最终发布目录运行 `scripts/validate-winui.ps1`：五个界面表面离屏加载成功且未激活窗口，`weatherCardControls.emptyStateRefresh` 为 `true`，空状态、深色与浅色三张离屏图片均生成且非空，探针未访问真实网络。

未验证：正常产品里对真实中国天气网刷新失败的可见反馈、任务栏卡片上真实鼠标点击空状态按钮、混合 DPI 与多显示器下的空状态排版。按项目规则没有启动日常安装或正常产品，上述仍按桌面验收处理。

## 单目录数据布局与迁移阶段（2026-10-22）

本轮将产品运行数据统一到 `<安装目录>\Data`，删除了 `global_cfg.ini` 模式读取和 AppData 回退。原生启动顺序现在是：验证 `Data` 路径与可写性 → 同步调用 `Media\TaskBarWidgets.Media.exe --migrate-data <安装目录>\Data` → 播种缺失的出厂文件 → 加载主配置和内置模块 → 启动媒体常驻进程。迁移失败不会加载旧配置或启动媒体。

配置仍按写入者分文件：`Data/config/app.ini`、`battery.ini`、`power.ini`、`weather.ini`、`media.json`；不可重建歌词数据在 `Data/lyrics/`，可重建缓存在 `Data/cache/lyrics/`。天气 AppKey 以当前用户 DPAPI 密文保存到 `Data/secrets/weather-appkey.bin`，活动 `weather.ini` 不保留 AppKey。原生与媒体端的密钥信封、UTF-16 明文和 Base64 编码已做双向兼容；旧 UTF-8 密文也会尝试读取。

迁移使用 staging、来源优先级和原子提交：目标现有文件优先，绝不覆盖；所有 JSON、INI、流量结构和 SQLite `integrity_check` 通过后才移动 staging 到目标，冲突及旧来源归档到 `Data/migration/archive/<UTC时间>/`，只保留最近一次归档。锁定文件不能删除时保留源文件并记录警告，不把已成功的新数据错误回滚。备份包含配置、历史、歌词不可重建数据、`layout.json` 和 DPAPI 密文，排除缓存、日志、归档和 SQLite 侧车；恢复会再次校验清单/哈希，生成 `pre-restore-latest.zip`，并在冷启动失败时回滚本次覆盖。

验证证据：完整硬件 Release 构建 `artifacts/builds/20261022-194522`（版本 `261022-1945`）通过，`Lyrics.Core`、`Media`、`UI` 和两个测试项目均为 0 warning／0 error。`run-native-tests.ps1`、`run-builtin-startup-test.ps1`、`run-weather-service-tests.ps1`、`run-update-health-tests.ps1`、出厂默认、默认播种、历史导入、数据快照和旧构建退休回归均通过。独立数据布局探针覆盖当前便携 `Data/database` 迁移、AppData 优先级、锁定源文件归档、活动天气 INI 清洗、SQLite 校验、二次恢复、恢复前快照和 SQLite WAL/SHM 清理；原生与 C# 的 DPAPI 文件此前已完成双向读取。更新健康测试新增旧版协议 1 作业兼容，覆盖 `261022-1616` 客户端发起的跨版本升级、已回滚作业不阻塞启动和仍处于安装中的作业继续触发恢复保护。`test-ui-templates.ps1` 对 `20261022-194522` 通过，设置页与四张卡片正常加载且未激活窗口；离屏 HWND 未把投递的鼠标消息提升为 `PointerPressed`，因此真实拖动仍未验证。独立更新器 `test-updater.ps1 -SkipBuild` 在当前机器无子进程输出并挂起，已中止；实际发布 ZIP 的完整解压、持久化阶段和更新器预检通过。

本地发布包验证（未发布）：完整解压、持久化阶段和更新器预检通过；ZIP 为 160 个条目、159 个程序文件，`Data`、`secrets`、`logs`、`migration`、`backups` 均为 0。具体本地路径与 SHA256见本轮任务结果。`dotnet test` 在当前机器启动 Testhost 时因 `OpenProcess` 返回 `Win32Exception (5) 拒绝访问` 中止，属于本机进程权限环境阻断，未观察到测试断言失败。

未验证：真实用户 AppData 的完整归档耗时、日用安装升级后的首次迁移、跨用户/跨机器 DPAPI 失败、桌面备份/恢复按钮、卸载/启动登记、多显示器混合 DPI 和真实播放器；这些仍按桌面验收处理，不把离屏或夹具通过当成完成。兼容修复没有启动日常安装执行第二次真实更新；本地安装仍保留旧版，最终更新链路由用户下次确认更新时完成。

## 资源占用优化阶段 5：UI 管道对端校验（2026-10-22）

OPT-009 单独提交落地，构建 `artifacts/builds/20261022-134854`。

- 校验只作用于 UI 桥管道 `TaskBarWidgets.UI.{mediaPid}`：[Media/Integration/MediaPipeServer.cs:40-49](Media/Integration/MediaPipeServer.cs) 推导期望对端为 `../UI/TaskBarWidgets.UI.exe`，`:76-81` 在握手与 generation 自增前调用 `AcceptClient`；`:124-157` 决定放行／拒绝。原生宿主管道 `TaskBarWidgets.{nativePid}` 的客户端是原生 exe，统一按 UI 校验会全部拒绝，故保持 `CurrentUserOnly` 并记一次 `skip/no-expected-peer`。
- 对端路径经 `GetNamedPipeClientProcessId` + `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName` 获取（`:161-197`），比较时大小写不敏感、归一尾分隔符并展开 8.3 短名（`:245-285`）；失败按 stage 打标，日志不含路径。
- 拒绝发生在 generation 自增前，不握手、不发帧、不触发 `_connectionChanged`；客户端首读 EOF，UI 500 ms 重连接管，协议版本与重连语义未变。
- 回退边界：同级 UI exe 缺失时 fail-open（记 `event=fallback`）；测试进程自连记 `skip/self-connection`。

验证：完整硬件 Release 构建 `artifacts/builds/20261022-134854` 通过；`tests/Media.Tests` 310/310（新增 `PipePeerTests` 7 测试／13 用例）。

未验证：真实第三方进程在断线窗口内连接 UI 桥管道 `TaskBarWidgets.UI.{mediaPid}` 被拒、被拒后 UI 重连时序、更新器替换 UI exe 期间的连接。残留缺口：原生宿主管道 `TaskBarWidgets.{nativePid}` 仅 `CurrentUserOnly`、无对端校验，断线窗口内同用户进程可占槽收发；对端路径比较不解析重解析点（目录 junction／8.3 短名可使 `QueryFullProcessImageName` 呈现期望路径字符串），同用户威胁模型内仅纵深价值；非标准发布布局会被拒；拒绝日志未限流。

## 资源占用优化阶段 4：Native 失败归因与任务栏刷新计数（2026-10-22）

OPT-005／OPT-006 单独提交落地，构建 `artifacts/builds/20261022-134854`。

- OPT-006 只加证据不改行为：[NativeMediaRenderer.h:60-84](Native/TrafficMonitor/NativeMediaRenderer.h) 新增 `FailureOp` 枚举，[NativeMediaRenderer.cpp:278-307](Native/TrafficMonitor/NativeMediaRenderer.cpp) 的 `RecordFailure` 以"操作名＋HRESULT"5 s 去重并累计 `failedOps`／`suppressedOps`；`FailureContext()`（`:310-322`）只在写日志时构造，一次日志带操作名、HRESULT、交换链尺寸、buffer 数与 `targetBound`。`IsDeviceLostResult` 仍未收录 `DXGI_ERROR_INVALID_CALL`，`Fail` 仍不排重试。
- 失败点补操作名（`CreateD3DDevice`／`ResizeBuffers`／`AcquireBuffer`／`CreateTargetBitmap`／`Draw`／`Present`／`SetContent`／`AddVisual` 等），`ApplyVisualTree` 的 `SetRoot`／`Commit` 失败计 `silentOps`，调用序列不变；`HandleDeviceLost`（`:1540-1558`）的判定与重试顺序不变。
- OPT-005 归因：[NativeMediaHost.cpp:1440-1495](Native/TrafficMonitor/NativeMediaHost.cpp) 的 `native-metrics.log` 行追加 `deviceLost`／`invalidCall`／`failedOps`／`suppressedOps`／`silentOps`／`lastFailureHr`／`lastFailureOp`／`lastFailureWidth`／`Height`／`Buffers`／`TargetBound` 与任务栏 `invalidates`／`adjustRectChanged`；后者由 [TrafficMonitorDlg.cpp:1835-1848](Native/TrafficMonitor/TrafficMonitorDlg.cpp) 在 `AdjustWindowPos` 前后各取一次窗口矩形得出。
- 本轮未调整任何节拍：三个定时器与 2 秒兜底发现机制原样保留。

验证：完整硬件 Release 构建 `artifacts/builds/20261022-134854` 通过；`Native/tests/run-native-tests.ps1` 全部通过（含 `NativeMediaRendererLifecycleTests`、`CompositionResizeTests`、`NativePipeTransportTests` PASS A–E）；`scripts/measure-footprint.ps1` 已把新累计字段加入计数器键表。

未验证：`0x887A0001` 的真实来源仍未定位（需真机出现时读 `native-metrics.log`）；100 ms 任务栏刷新与 1 s 主定时器能否降频尚未量化；`m_diagnostic` 只在真正写日志时更新，去重窗口内的重复失败不刷新诊断文本。

## 资源占用优化阶段 3：UI 常驻资源边界（2026-10-22）

OPT-003 单独提交落地，构建 `artifacts/builds/20261022-134854`。

- 释放门槛改为"资源类型＋隐藏时长"：[UI/CardGroup.cs:1226-1252](UI/CardGroup.cs) 只判 `_disposed || _visible || _residentResourcesReleased`，Resident 模式隐藏满 10 分钟（`:117`）同样释放；不足 10 分钟则按剩余时间重新武装单发定时器（`:1254-1260`）。
- `_idleReleaseTimer` 由 30 s 周期改为 10 分钟单发（`:165-171`），打开时 `Stop()`（`:346`）、隐藏时交由释放判定决定是否武装（`:407`），Resident 下定时器空转消失。
- 释放集合按优先级重排：保留 `_cover.Source`（当前曲目封面位图），释放歌词排版、来源图标位图与缓存并置 `_forceArtworkSync`；删除原 `_cover.Source = null`／`_lastCover = null`，避免首开重新解码并闪空白帧。
- 设置页关闭释放内容树：[UI/SettingsWindow.cs:306-359](UI/SettingsWindow.cs) 两个静默关闭分支统一 `CloseToHiddenWindow()`，`Hide()` 后 `ReleasePageContent()`（`:363-388`）清空页面子树并置空预览面，保留窗口壳；有未应用草稿时跳过。重建走 `Activated`／`ShowPage` → `RenderPage()`（`:183-189`、`:654-660`）。
- 日志：`resident-resources-released reason=idle-hidden mode=… hiddenS=… elapsedMs=…`、`settings-content-released`、`settings-shown reused=`（[UI/App.xaml.cs:234-251](UI/App.xaml.cs)）。

验证：完整硬件 Release 构建 `artifacts/builds/20261022-134854` 通过；`scripts/validate-winui.ps1` 通过（含"关闭后仍是隐藏窗口"的模板断言）；`dotnet build` UI 项目 0 warning 0 error。

未验证（桌面验收）：隐藏 10 分钟后首次打开的 p50／p95 与内存回落幅度；关闭瞬间在途 `SaveAsync` 的 `finally → RenderPage()` 是否把树重建回隐藏窗口；清空焦点控件后的焦点回落；更新卡与真实换曲路径。

## 资源占用优化阶段 2：频谱分配（2026-10-22）

OPT-004 单独提交落地，构建 `artifacts/builds/20261022-134854`。

- [Media/SystemAudioSpectrumService.cs:156-228](Media/SystemAudioSpectrumService.cs) 的 `GetSpectrum` 改为每帧从 `_idleScratch` 租借 scratch 缓冲（`Interlocked.Exchange` 取、`finally` 归还），`SpectrumScratch`（`:756-802`）仅在声道数或采样窗变化时重建；`powers`／`real`／`imaginary`／`magnitudes`／`bars` 全部复用到一次性分配的缓冲里。
- 整帧只在第一段 `lock (_sync)` 内捕获一次 `_tuningSettings` 引用（`:172`），删掉每次 `GetSpectrum` 的 `Clone()`；`ApplyTuning` 仍是"先 Clone 再整体替换引用"，语义不变。
- Hann 窗按长度缓存在静态 `ConcurrentDictionary<int, double[]>`（`:28`、`:582-592`），不再逐样本重算余弦。
- 输出仍 `Clone()` 一次（`:222`），因为 `MediaCoordinator.cs:621` 会把数组跨帧保留。

收益（分配器口径折算，**非**桌面复测；2 声道、2048 窗、32 bars，对象头 24 B、double 8 B/元素、float 4 B/元素）：每帧分配 `samples` 40 B＋每声道 `float[2048]` ×2 共 16,432 B＋`powers` `double[1025]` 8,224 B＋两声道 `real`／`imaginary` `double[2048]` ×4 共 65,632 B＋`magnitudes` `float[1025]` 4,124 B＋输出 `bars` `float[32]` 152 B＋调参 `Clone()` 196 B＝94,800 B → 152 B（62.5 fps 时约 5.65 MiB/s → 9.3 KiB/s）；形状变化才付一次约 61.8 KB（61,788 B）的 scratch 重建。

验证：完整硬件 Release 构建 `artifacts/builds/20261022-134854` 通过；`tests/Media.Tests` 310/310（新增 `SpectrumSignalTests` 5 项：逐帧对照新实例、同输入两次一致、声道形状变化、窗口长度切换、返回数组归调用方）；`Native/tests/run-native-tests.ps1` 全部通过。

未验证：真实设备切换、系统音频授权后首次起采、静音／停止播放的节拍与听感未做桌面复测；分配数字来自 `GC.GetTotalAllocatedBytes`，不含原生侧分配。

## 资源占用优化阶段 1：封面与状态增量（2026-10-22）

OPT-001／OPT-002／OPT-007／OPT-008 一次提交落地，构建 `artifacts/builds/20261022-131902`。

- OPT-001 Native 封面按显示尺寸解码：`PrepareCover` 改收 `coverEdgePx`（[NativeMediaPainter.h:105](Native/TrafficMonitor/NativeMediaPainter.h)），解码边取 `m_effectiveStyle.coverSizePx`（物理像素），由 [NativeMediaRenderer.cpp:847-850](Native/TrafficMonitor/NativeMediaRenderer.cpp)／`:951-953` 传入；[NativeMediaPainter.cpp:948-1023](Native/TrafficMonitor/NativeMediaPainter.cpp) 的 `DecodeBitmap` 只在源图更大时用 `IWICBitmapScaler`（`HighQualityCubic`）等比缩小，2048 边／4 MPx 拒绝原样保留；缓存键混入解码边长（`:844-850`），淘汰由"最多 4 条"改为 4 MiB 字节预算（`:864-882`）。
- OPT-002 `getState` 增量：新增 [Media/Integration/ArtworkDeltaState.cs](Media/Integration/ArtworkDeltaState.cs)，[Media/App.xaml.cs:199-230](Media/App.xaml.cs) 按连接 generation 省略未变的 `coverDataUrl` 与 `sources[].iconDataUrl`；新连接与不声明 `artworkDelta` 的客户端仍收全量，旧客户端不会因缺字段清空封面。[UI/CardGroup.cs:773](UI/CardGroup.cs) 声明 delta，`:836-839` 与 `:1252` 把缺失字段当"未变"，本地释放资源后 `:1232` 强制一次全量。
- OPT-007 隐私：[NativeMediaHost.cpp:34-49](Native/TrafficMonitor/NativeMediaHost.cpp) 用 FNV-1a 64 `TrackHash64` 替代 `trackId` 全文，`:946-958` 只写长度与哈希标记；日志不再含标题或艺人。
- OPT-008：删除 `MediaCoordinator.CreateCover()`、`_coverBitmap` 与 `MediaControlState.Cover`（无消费者的检索证据见[方案 §5](docs/2026-10-22-资源占用优化方案.md)）。

收益（折算自阶段 0 实测口径，**非**本轮实测）：`getState` 稳态不再发送约 88 KB 图标 + 5.3 KB 封面（卡片可见时 2.34 次/s ≈ 0.28 MB/s 管道字节）；Native 单张封面 360,000 B → 34×34×4 = 4,624 B（100% DPI，200% 为 18,496 B）；封面缓存上限从 4 条 × 2048² × 4 B 的理论 64 MiB 收到 4 MiB 硬预算。

验证：完整硬件 Release 构建 `artifacts/builds/20261022-131902` 通过；`tests/Media.Tests` 292/292（新增 `ArtworkDeltaTests` 5 项）；`Native/tests/run-native-tests.ps1` 全部通过（含新增缩放／缓存键断言 [NativeMediaOffscreenTests.cpp:321-343](Native/tests/NativeMediaOffscreenTests.cpp)）；`scripts/validate-winui.ps1` 通过。

未验证：本轮未做桌面复测（用户选择先提交、复测留到桌面验收）。换曲 20 次后的 PWS/Commit 斜率、100% 以外 DPI 的重解码与交叉淡入、索引色 GIF/BMP 的缩放回退、任务栏很窄时 `min(coverSizePx, shellWidth)` 的多余解码、`appIcon` 透明边在 `HighQualityCubic` 下的暗边、重连后封面正确性（仅单元测试覆盖）均未在桌面确认。

## 资源占用优化阶段 0：测量仪表与基线（2026-10-22）

Optimization 分支的阶段 0 只加观察能力，不改产品行为：新增外部采样脚本与三进程内建计数，用于后续 OPT 编号工作项的 Δ 与斜率判据。

- 新增 [scripts/measure-footprint.ps1](scripts/measure-footprint.ps1)：按固定 1 s × 60 s 窗口对 `TaskBarWidgets`、`TaskBarWidgets.Media`、`TaskBarWidgets.UI` 采集 Working Set、Private Working Set、Commit、线程数、句柄数与 CPU 时间增量，输出首末值、Δ、均值与最小二乘斜率，并解析三进程的内建计数行；产物写入忽略目录 `artifacts/measurements/`。默认拒绝采样路径不在目标构建目录内的进程，避免把日常安装当成基线。
- Private Working Set 改走 PDH `\Process(*)\Working Set - Private`（按 `ID Process` 关联 PID）。`QueryWorkingSetEx` 在本机对 explorer 与自身进程都返回成功但缓冲区全 0，已弃用；实测 explorer 339.3 MB / 97.3 MB、当前 shell 163 MB / 70.7 MB 与任务管理器一致。
- Media 内建计数（`Media/Diagnostics/FootprintMetrics.cs`，5 s 一行 `[METRICS]`）：每秒分配字节、gen0/1/2、FFT 次数、发布帧数、管道写入次数与字节、`getState` 次数／总耗时／最大耗时／文本字节／封面字节／图标字节。
- Native 内建计数（`NativeMediaHost::WriteFootprintMetrics()`，5 s 一行，写 `<安装目录>\Data\logs\native-metrics.log`）：活动图层数、交换链数、文字排版／格式／画刷条数、封面缓存条数与字节、`MAIN_TIMER`／`MONITOR_TIMER`／`TASKBAR_TIMER` 触发次数。该文件不含曲目、路径或媒体内容。
- UI 内建计数（`UI/FootprintMetrics.cs` 与 `CardMetricsLog`）：首开次数、首开与首帧的累计／均值／最大、首帧超时次数、图片缓存条目数、封面与来源图标是否在位。每 10 s 一行，打开与关闭时各补一行。
- 计划文档新增 [§3.5 阶段 0 实施结果](docs/2026-10-22-资源占用优化方案.md)，记录字段、采集点与统计口径，并列出与方案原文不一致处（Native `Diagnostic()` 不承载计数、UI 未加 HWND 计数）。

- 阶段 0 基线已实采（同一实例 12:35:48–12:42:30，403 点/进程）：待机三进程 PWS 合计 147 MB；播放歌词后 Native +25.1 MB 且停止播放后不回落；设置页首次打开一次抬升 UI +75.3 MB PWS／+85.1 MB Commit／+626 句柄，关闭后不回落；最高状态为"播放过并开过设置之后的待机"295 MB；频谱把 Media 分配从 0.40 抬到 4.75 MB/s。明细与未采集项见 [§3.5](docs/2026-10-22-资源占用优化方案.md)。

验证：完整硬件 Release 构建 `artifacts/builds/20261022-120729` 通过；`tests/Media.Tests` 287 项通过（须先执行 `Native/tests/run-native-tests.ps1` 生成 `NativePipeIntegrationClient.exe`，否则该集成用例报缺失）；`Native/tests/run-native-tests.ps1` 全部通过；`scripts/validate-winui.ps1` 实际模板加载通过。

未验证：11 个状态里"换曲"未采集（窗口内无换曲特征）与"隐藏十分钟"未采集（压缩为 60 s 并并入停止播放段）；启动态窗口比进程创建晚约 20 s，冷启动峰值未覆盖。真实切歌、Explorer 重建、音频设备切换、多显示器混合 DPI、输入延迟属桌面验收，本轮未做。用户报告的"获取系统音频信息失败"提示在仓库中检索不到原文，其触发路径未复现；仅确认重新授权后 FFT 起采（§3.5 附表）。
## 当前结论：Mica 观感已获用户认可（2026-10-21）

用户明确反馈“现在的 mica 我比较满意了”，当前环境下的 Mica 视觉效果据此记为已认可，本轮视觉调校收尾。保留 Mica 以及现有个人参数和出厂默认值；未提供的实际个人参数不作推断，实验候选不自动成为默认。

当前界面为“自定义 Mica”开关，浅／深色各有“中性色遮罩”“明暗调和”两个滑块，直接对应底层参数。设置窗口与卡片共用真实控制器，系统模式使用运行时默认，移除了近似 `SystemProxy`；设置页去除重复底色。完整外观草稿保护开关与四个值，合法 0／1 端点和显式保存的配置不再被迁移重置。

确认范围限于当前 Mica 观感。跨显示器／混合 DPI、Explorer 重启、系统高对比度与全局透明效果切换，以及真实音频、播放器和完整桌面更新重启等场景，不因这次视觉认可而自动记为通过。历史章节中的 Mica 参数、迁移规则及“待验收”结论是当时记录；当前行为以本节、[产品说明](README.md#卡片图标与材质)和[界面规范](docs/界面统一方案.md)为准。

## 更新兼容修复与发行状态（2026-10-21）

- 当前正式 Release：[261021-1147](https://github.com/NoctisWang528/TaskBarWidget/releases/tag/261021-1147)，源码提交 `7da6343`；Mica 功能修复提交为 `7460af5`。
- `261021-1120` 因新增资源 `UI/Settings/SettingsNavigationResources.xbf` 被旧版个人目录保护规则拒绝，已撤回 GitHub Release；Git 标签和源码历史保留，不作为下载推荐。这是程序资源路径不兼容，不是发行包混入个人数据。
- 资源已迁移至 `UI/Resources/SettingsNavigationResources.xbf`，没有放宽个人数据保护。最终包确认不存在旧路径，WinUI 实际模板加载通过。
- `261021-1147` 完整硬件 Release、39 项更新定向测试、共享运行库及最终包 WinUI/Mica 检查通过。此前 Mica 修复的 71 项配置存取测试和十组同位置实屏对比记录保留；用户视觉反馈与自动检查分开记载。
- 打包流程已接入真实 `ExtractAndVerify`／`VerifyStage`；从 GitHub 草稿重新下载的完整 ZIP（188 个程序文件）分别通过官方 `261021-1031`、`261021-1120` 发布程序集的实际解压与暂存复核。错误的 1120 ZIP 能被同一验证入口拒绝，正常的 1031 ZIP 能通过。
- 原生隔离检查使用完整新清单和旧版安装清单到达 `readyToExit`，未批准退出，确认没有进入替换或启动包内产品；其余事务／回滚用例使用模拟宿主。不能据此认定真实日用安装的退出、升级、重启及数据保留链路已由用户验收。

整包验证及 draft → 下载复核 → 正式发布流程见[更新协议](docs/更新协议.md)。诊断记录、ZIP、截图和个人数据均留在本地忽略的 artifacts 中。

## 图标裁剪、歌词块透明与真正生效的鲜艳 Mica（2026-10-20）

用户反馈三项：媒体卡歌词块仍有底色、设置／刷新／静音按钮仍被截断、鲜艳 Mica 看起来没有生效。三项都定位到具体实现缺陷，而不是参数微调。

**图标截断**：`PathIcon` 按自身尺寸直接绘制并裁掉超出部分，而 `CardIconArt` 的图形按 20 单位网格绘制。32 DIP 圆形按钮此前把 `PathIcon` 设成 16 DIP，设置齿轮、扬声器波纹、刷新箭头和上一首／下一首的最外圈因此被切掉（播放按钮 42 DIP 内仍为 20 DIP，所以一直正常）。现在每个图标都用固定 `Viewbox` 承载完整 20 单位图形：32 DIP 按钮缩放为 16 DIP，42 DIP 播放按钮保持 20 DIP；宿主仍为 20 DIP 居中、`Padding=0`、`MinWidth/MinHeight=0`，静音与播放状态继续通过切换矢量图形更新。`CardAudioProbe` 新增断言：每个图标的几何必须完整落在 20 单位绘制网格内，且必须由固定尺寸缩放器承载（本轮 7 个宿主），同时继续校验中心误差不超过 0.5 DIP。

**歌词块底色**：底色不是来自网页样式（`html/body/.layout` 计算背景本来就是透明），而是 WinUI `WebView2` 的 system visual bridge——它用 `SolidBackgroundFillColorBase` 填充承载网页的合成视觉，网页透明时就露出一整块不透明底色。`CardLyricsView` 现在把渲染器资源 `BrushForThemeBackgroundColor` 覆盖为透明画刷，并在注入样式中把 `html/body/#layout.layout/.shell/.lyrics-pane/.lyrics-layer/.viewport/.track` 的背景与阴影强制为透明。桌面取像素验证：歌词块与卡片材质同色，不再出现白色矩形。歌词探针新增两项断言：渲染器资源必须是 alpha 0 画刷，`body` 与 `.layout` 的计算背景必须是 `rgba(0, 0, 0, 0)`。

**鲜艳 Mica 为什么“没生效”**：材质一直生效，问题在中性 tint 的浓度。新增的桌面验收工具 `scripts/mica-visual-check.ps1` 让 UI 进程在真实桌面显示一排卡片窗口并按窗口矩形取像素（DWM 合成的 Mica 不在 XAML 视觉树里，离屏渲染永远看不到它）。实测（浅色、彩色壁纸）：`tint 0.45 / luminosity 0.95` 是接近不透明的浅灰面板，`tint 0.10 / luminosity 0.85` 能清楚透出壁纸颜色，`tint 0.25` 起明显发灰；深色模式 `tint 0.15` 比 `0.45` 更能带出壁纸色相。用户旧配置正好是浓度 45%／通透度 10%，落在完全不透明的范围内，所以新版本看起来和旧版本一样。新默认改为浅色浓度 10%／通透度 15%、深色浓度 15%／通透度 10%（`tint 0.10 / luminosity 0.85`、`0.15 / 0.90`），并新增“鲜艳 Mica”开关（默认开启）：关闭后卡片改用系统默认 `MicaBackdrop`，四个滑块停用；设置窗口预览用 `CardMicaTuning.SystemProxy`（浅色 `0 / 0.85`、深色 `0.15 / 0.96`；当时的固定近似，不能代表所有运行时的系统默认，已在 2026-10-21 移除）近似系统材质。`quickCards.mica` 新增 `vivid`，设置命令新增 `cardMicaVivid`。

旧配置一次性迁移：文件中缺少 `CardMicaVivid` 即视为“鲜艳材质之前的配置”，四个数值一次性重置为新默认（旧值是按几乎不透明的旧观感调的），写入开关后不会再重置；已有该字段的配置保留用户自选值，显式深色字段优先和 `-0.02` 旧偏移兼容逻辑不变。工厂 JSON 与 `media-settings.default.json` 同步为新默认，仍只在整份配置缺失时种入。

自动验证：`verify.ps1` 全部通过（Core 27/27、Media 167/167、Web 28/28、原生行布局与出厂默认检查）；新增用例覆盖鲜艳开关往返、缺少开关时的一次性迁移、迁移后自选值不再被重置。完整硬件 Release 构建为 `20261020-233109`、版本 `261020-2331`，`validate-winui.ps1` 针对该构建通过：`ui-template-check.json` 为 `success=true`，`audioControls.scalerIcons=6`、`iconsUnclipped=true`、最大中心误差 `0`，`micaControls.vividSwitch=true`、`vividRoundTrip=true`，歌词探针包含透明桥资源与网页计算背景断言。桌面取像素证据为 `artifacts/builds/20261020-233109/ui-mica-check/desktop.png`：新默认浅色卡透出壁纸色、旧 45%/95% 仍是灰白面板，歌词块与卡片同色。仍需桌面验收：真实壁纸下的最终观感与开关取舍、系统浅色／深色即时切换、真实音频设备静音图标、混合 DPI、Explorer 重启后的表现。本轮未启动正常产品、未替换日用安装、未修改启动项。

## 卡片矢量图标、鲜艳 Mica 与歌词偏移加载（2026-10-20）

四张快捷卡片和天气刷新按钮的圆形图标改由 `UI/Cards/CardIconArt.cs` 绘制（上一首、播放／暂停、下一首、扬声器、扬声器静音、麦克风、麦克风静音、设置、刷新），不再依赖 Segoe Fluent Icons；静音状态通过切换矢量图形更新，不读取或修改字体字形。所有圆形按钮使用固定 20 DIP 图标宿主（32 DIP 按钮内 16 DIP，42 DIP 播放按钮内 20 DIP），`Padding=0`、零最小尺寸并关闭布局取整，修复字体缺失时的方框和内容偏移；下拉箭头、菜单勾选等非圆形系统图标保持不变。天气卡主题切换改为在 `ActualThemeChanged` 后确定性重绘彩色图标和预报模块。

Mica 按 MicaTabless 的“内容透明、系统材质直接透出”原则实现：浅色和深色都只保留 `#0AFFFFFF` 的首帧防白闪层，内容不再叠加整卡底色；浅色使用 `MicaKind.Base` 与中性 `#F3F3F3` tint，深色使用 `MicaKind.BaseAlt` 与中性 `#202020` tint，fallback 同步中性化。材质设置拆为浅色和深色两组“色彩浓度／通透度”滑块，共四个（0–100%），默认浅色 45%／5%、深色 45%／50%；预览和现有卡片按当前实际主题使用对应组，点击“应用更改”后持久化四个值。旧文件的两个字段继续作为浅色配置：旧工厂值精确为 `1.0/1.0` 时迁移到新默认，其他自定义值保留浅色并按旧实际效果 `-0.02` 初始化深色；显式深色字段优先，非法值钳制，迁移只在内存中，下一次正常保存才写回。`quickCards.mica` 新增 `darkTintOpacity/darkLuminosityOpacity`，旧两字段载荷按旧偏移回退。

“歌词 → 单曲偏移”改为显式页面激活生命周期：`ShowPage`、导航选择与设置窗口重开统一经 `EnterPage`，每次进入先渲染缓存再发送恰好一次 `trackOffsetsPageActivated + queryTrackOffsets`。搜索／刷新和翻页只查询；查询带单调递增 `requestId`，同一时刻最多一个请求加一个 latest-wins 尾随刷新，页面代次、离开页面或请求过期时丢弃整批响应，每次有效批次只最终渲染一次。`RenderOffsets` 不再在缓存缺失时自动加载。

自动验证：`SettingsStoreTests` 覆盖新默认、`1.0/1.0` 工厂迁移、自定义旧值、显式深色优先、钳制、四字段往返和不覆盖既有文件；Media 测试 165/165 通过，UI 管道检查通过。屏外模板探针 `artifacts/builds/20261020-220043/ui-template-check.json` 为 `success=true`：6 个圆形图标宿主全部测得，最大中心误差 `0`；四滑块、深浅独立、默认值、旧格式兼容、预览与丢弃恢复全部通过；偏移页首次进入 `initialActivations=1`、`initialBatches=1`、`initialFinalRenders=1`，重绘查询 `0`，同页重开激活 `1`，导航风暴合并为 `1`，过期进入／请求被忽略，搜索／翻页不激活，`requestId` 单调且每批只最终渲染一次。最终完整构建、`validate-winui.ps1` 和最终发布包模板探针按 `artifacts/latest-build.txt` 指向的目录执行，结果以该构建的检查产物为准。

桌面验收仍待完成：真实壁纸下的 Mica 鲜艳度与通透度、系统浅色／深色即时切换、真实输出／输入设备静音图标、混合 DPI、Explorer 重启后的卡片表现，均不能由离屏截图或模拟设备替代。本轮未启动正常产品、未替换日用安装、未修改启动项；提交、推送与 Release 按用户的后续明确授权执行。

## 出厂默认配置与彩色天气图标（2026-10-20）

出厂默认配置改为编译期资源，不再依赖代码兜底：`Native/TrafficMonitor/defaults/` 下四个 `*.default.ini`（主配置与 Battery／PowerMon／Weather 三个内置模块）以 TEXT 资源编入 `TaskBarWidgets.exe`，`Media/Defaults/media-settings.default.json` 以嵌入资源编入媒体程序集。首次运行时只对**不存在**的目标文件逐字节写入，写入使用同目录临时文件加无覆盖改名；已有配置在启动、更新和重装路径上都不被读取、合并或覆盖，目录只读时静默回退。默认值已通用化：不写网卡描述（`connection_name`）、CPU 温度传感器名、任务栏字体、窗口位置、凭据与个人路径；天气保留通用默认城市北京（与代码兜底一致），用户设置后以用户城市为准。`cpu_core_name` 留空时按“平均温度”处理，`hard_disk_name` 为 `_Total`。同时修正 `global_cfg.ini` 首次写入只留 BOM 空文件、导致下次启动误判为非便携的问题。发布脚本、更新器和 `app-files.json` 未改动，默认值不会作为包内文件出现。

天气卡改用两套调色板的分层彩色矢量图标（`UI/Cards/WeatherIconArt.cs`，云体＋云影两层，太阳/月亮/降水按状态组合），替换 `PathIcon` 与 QWeather 字体轮廓，`qweather-icons.ttf` 不再打包；大图标 42 DIP、预报行 28 DIP，主题切换时重绘。当前温度按列宽自适应字号（48→26 DIP）并在图标前保留 6 DIP 间距，修复一位小数温度压住度数符号的问题。

自动验证：`check-factory-defaults.ps1`（编码、禁用键与个人串检查）、`test-default-seeding.ps1`（以数据文件方式打开发布 EXE，验证资源存在、与源文件哈希一致、只创建缺失文件、已修改配置不被覆盖、无临时残留）、`FactoryDefaultsTests`（媒体侧三例）与 `verify.ps1`（含 27 个 Core、158 个 Media、28 个 Web 用例）全部通过；`DefaultSeedingTests.exe` 对新构建 EXE 运行确认 349–352 四个 TEXT 资源已嵌入且与源文件逐字节一致。完整硬件 Release 构建为 `20261020-201506`、版本 `261020-2015`（原生、Media、UI、独立更新器与共享运行库全部通过），`package-release.ps1` 零改动产出干净 ZIP，`app-files.json` 中不含任何 `*.default.ini`、`config.ini`、`media-settings.json` 或 `qweather-icons.ttf`。屏外模板探针本轮未执行：本机日用版本正在运行并占用共享的 `CardWebView2` 配置目录，需先退出日用版本才能完成歌词卡片阶段；因此彩色图标与温度自适应目前为编译与探针断言级证据，卡片观感、混合 DPI 与真实天气数据仍需桌面验收。

## 天气图标方框修复（2026-10-20）

针对天气卡 QWeather 图标在部分实际合成路径中显示为方框的问题，图标不再通过私有区字体字形渲染，而是使用 QWeather Icons 字体轮廓生成独立矢量路径。当前与五天预报的全部 15 个图标映射保留；几何按 28/42 DIP 目标视口缩放并保留 2 DIP 内边距，避免 `PathIcon` 按原始字体坐标裁切。浅色与深色主题均由 `PathIcon` 自动继承前景色，不依赖运行时字体回退或字形缓存。回归探针检查全部字形在两个目标尺寸下均不越界，并生成当前天气和五天预报的明暗主题截图。完整硬件 Release 构建为 `20261020-135915`、版本 `261020-1359`；原生、Core、Media、Web、更新器、共享运行库和 WinUI 屏外模板探针全部通过，隔离 `CardWindow` 的 `PrintWindow` 捕获也确认图标实际显示。真实桌面天气请求和用户卡片验收仍待完成。

## 卡片弹出预热与首帧门控（2026-10-20）

针对“后台资源已选常驻但打开卡片仍卡顿”的首开约 500 ms 延迟和白色闪屏，UI 进程在管道连通约 300 ms 后读取媒体侧快捷卡片配置，分批创建窗口、按隐藏态测量放置、以零透明度渲染一帧后立即隐藏；点击路径只剩定位、显示和 150 ms 淡入。显示前根视觉透明度为 0，`AppWindow.Show(false)` 后等待一次合成帧（250 ms 超时）才启动淡入，重开与隐藏动画仍使用世代校验。卡片内容区改用主题资源 `CardBackgroundFillColorDefaultBrush` 填充，Mica 保留在卡片外侧。新增有界 `UI/card-timing.log` 记录定位、显示、激活和首帧阶段耗时。完整硬件 Release 构建为 `20261020-121923`、版本 `261020-1219`；原生、Core、Media、Web、共享运行库与 WinUI 屏外模板探针全部通过。预热不初始化歌词渲染器，也不请求流量、天气或音频数据。真实桌面连续开合耗时、白光观察、混合 DPI 和 Mica 仍待用户验收。

## 天气卡 Apple 风格布局（2026-10-19）

天气快捷卡片已改为固定 Apple 天气小组件风格：约 2:1 横向内容、直接使用卡片窗口的 Mica 背景，不叠加内部圆角框或独立标题；左栏显示城市、紧邻的刷新按钮、当前温度和天气状态，右栏最多五行预报。左下角预警区按 0/1/多条分别留空、固定显示、每 4 秒轮换显示一条；浅色和深色模式均使用 QWeather Icons 矢量字体并保持文字清晰。不再提供天气卡模块自定义，旧 `weatherCardModules` 配置在保存时忽略并移除。快捷卡片共用可在“快捷卡片 → 卡片与顺序”设置的卡片字体。完整硬件 Release 构建为 `20261019-223130`、版本 `261019-2231`；已通过完整自动验证、WinUI 组件检查和最终发布包屏外模板检查。真实 Mica、天气服务、鼠标拖动、混合 DPI、Explorer 和设备操作仍待桌面验收。

## 快捷卡片、天气卡与按需资源（2026-10-19）

最终候选完整构建为 `20261019-180147`，版本 `261019-1801`；已通过完整硬件 Release、原生与天气回归、Media/歌词、Web、共享运行库及正式包模板检查。以下功能与边界适用于该候选。

新增“快捷卡片 → 卡片与顺序/天气”设置分类。四张普通卡片使用稳定 ID `weather`、`traffic`、`device`、`media`，可独立启停和拖动排序；全部关闭时任务栏入口无响应，保存前会再次确认。关闭的单卡会关闭其 HWND 并释放歌词渲染器、流量合成特效或天气图标缓存，重新启用时用保留的 XAML 内容重建窗口。更新提示卡为独立的 220 DIP 第二列，只在 `ready` 时出现并与主列底部对齐，不进入普通卡片排序。

天气设置从硬件页迁入“快捷卡片 → 天气”，城市、来源、API Host/Key 与显隐选项继续使用 `WeatherPro.ini`；`show_weather_in_tooltips` 在读取迁移时清理，天气提示摘要生成路径不再调用。天气卡使用原生层提供的结构化 `details` 快照和中性 `conditionKind`，固定采用 Apple 风格布局；缺失数据不显示占位，预警在独立区域滚动展示。

资源模式为“常驻（优先速度）”与“按需（优先内存）”，默认常驻。常驻保留歌词 WebView2 和图片缓存，并在可用时订阅内存压力事件、必要时将 WebView2 降为低优先级；按需模式在卡片组隐藏 10 分钟后释放歌词渲染器和封面/来源图标。天气图标使用静态矢量字体，不维护图片缓存。WebView2 环境在重建时复用，卡片首帧使用最近快照缓存，`getState` 与 `getTrafficHistory` 并行请求；`togglePanel` 在 UI 打开前携带快捷卡片配置。

本轮新增或扩展的检查包括：媒体设置中的快速卡片顺序/启停/资源模式/模块往返与未知键规范化；已有 `media-settings.json` 缺少快捷卡片配置时启用天气卡；原生天气 `details`、revision、stale 保留和两来源缺失字段回归；屏外设置/四张普通卡/天气卡模板、更新卡第二列几何与主题、音频控件、歌词渲染器探针；共享运行库和内置功能初始化检查。实际桌面卡片拖动、真实天气网络、真实播放器和设备、混合 DPI、Mica/Composition 泛光、长期闲置后的打开耗时以及真实更新重启仍待用户验收；本轮没有启动正常产品、覆盖日用安装或发布 Release。

## 更新器进程路径误判修复

实机 `261018-1638 → 261018-1651` 在退出前报告 “Process ID no longer belongs to this installation”。读取作业 PID 和 QueryFullProcessImageName 证实三个进程均属于同一安装；原生更新器拼接 Media/UI 子路径时保留 `/`，却与系统返回的 `\` 路径直接比较。统一使用词法规范化后的通用路径进行不区分大小写比较，保留完整安装目录与程序名校验。

新增三个真实模拟进程分别位于根、Media、UI 目录的回归：修改前复现同一错误，修改后正常退出并完成隔离升级；其他安装目录的同名进程仍在任何替换前被拒绝，且不被关闭。已有备份、回滚、中断恢复检查继续通过。生成独立静态 CRT 修复更新器，嵌入管理员清单，未启动正常产品或替换日用文件。

本轮重新构建完整硬件候选 `261018-1709`，正式更新器已由修复源码重新编译。发布使用新的完整 ZIP 及更新元数据，不替换旧 Release 附件；新包内包含修复，避免升级后再次人工替换更新器。

这类错误发生于旧安装自带的更新器，因此仅发布新的下载包不能修复现有旧更新器。若用户继续测试已有正式版，需手动替换当前安装的 `TaskBarWidgets.Updater.exe` 后重新检查更新；已发布 `261018-1651` 仍带旧更新器。本次用户已授权提交、推送修复并重新构建发布完整版本；新发行包直接包含修正后的更新器，升级后不再需要二次替换。尚未修补旧安装的用户仍需先应用一次独立更新器补丁，或手动完整升级。真实桌面升级仍待反馈。

## 应用更新链路

新增固定 GitHub 正式 Release 的检查、下载、ZIP／逐文件校验、准备安装及独立原生更新器。默认自动检查并下载；安装必须由关于页或第四张提示卡进入同一个明确确认流程，安装前处理设置草稿、天气待保存内容和进行中的天气操作。卡片仅在原面板打开且更新就绪时出现，准备安装后移除，不自动打开或发送系统通知。首次从没有更新器的旧版本升级仍需手动完整解压并迁移数据。

`20261018-162356` 构建已执行 `scripts/validate-winui.ps1`，正式包诊断入口退出成功。设置与三张基础卡均实际加载且内容非零；另单独加载了可选 72-DIP 更新卡，验证在流量卡上方、同宽／间隔、浅深主题、内部点击分类、重复点击交给 App、准备阶段移除。关于页检查默认开启、进度原位更新、草稿保留及天气操作／未保存密钥防护；App 检查嵌套 payload 回退及过期／相同 revision 拒绝。探针仅用模拟配置，没有调用真实更新安装、个人天气密钥或声卡；`probeActivated=false` 且前台未变化。

该候选从系统共享 Core `8.0.31` 与 Windows App Runtime `2.5.1.0` 加载。离屏实际鼠标拖动仍不可验证：PostMessage 未被提升为 WinUI PointerPressed，不记为通过。本次 UI 探针不覆盖后续 Media／Native 修改；最终完整包仍应重复定向检查。更新器的模拟进程／隔离目录回归与真实桌面安装必须分开记录，不能据模拟结果声称真实退出、升级及回滚已验收。

**桌面更新待验收**：从受控正式 Release 完整检查／下载、管理员进程正常关闭、原安装目录替换与自动重启、Native+Media+UI 健康握手、保留真实配置／流量／SQLite 数据、依赖缺失提示、更新中断恢复与失败回滚，以及多屏混合 DPI 下第四卡的空间／命中和实际材质。当前没有为这项验收启动正常产品、覆盖日用安装或更改启动任务。完整行为与 draft→上传→下载校验→正式发布流程见[更新协议](docs/更新协议.md)。

### 最终候选 261018-1638

完整硬件 Release 构建 `20261018-163827` 通过。最终产物再次执行 WinUI 正式模板探针、共享 .NET／WinUI 和原生依赖失败检查；更新卡出现／准备后移除、关于页进度与草稿保护、消息序号过滤均通过，未激活窗口。完整发行 ZIP 经产品的 `--validate-update-package` 诊断入口验证成功，包含 184 个受管文件；个人包与干净包的程序文件哈希一致（个人 README 单独说明），13 份个人数据快照核对通过，SQLite 使用一致性备份。

下载协调器的针对性行为检查覆盖版本、限流、取消、损坏哈希、异常 ZIP、恢复缓存及继续发现后续版本。原生更新器在独立目录、模拟宿主下覆盖正常升级、退出超时、文件占用、替换失败、启动失败、数据回滚、恢复及备份轮换；原生握手另验证真实模拟进程映像、nonce、目录、持久化确认和中断事务阻止启动。测试程序嵌入 asInvoker；正式更新器嵌入 requireAdministrator，静态 CRT，约 563 KiB。以上不等同于真实产品进程完整升级验收。

本次个人数据来自上一版 `261018-1514/build` 的已恢复快照，保留旧包；未修改源实例、自动启动注册、Git 提交或远端。干净 ZIP 为约 29.2 MiB，SHA256 与大小同时写入旁边的 `update.json` 和 `SHA256SUMS.txt`。版本没有发布到 GitHub；实际正式 Release 下载与用户确认后的桌面升级仍按上述待验收清单执行。

### 发布验证与升级测试

用户随后授权提交、推送本轮更新功能，并以新构建时间重新构建、发布干净正式 Release，供已有 `261018-1638` 手动检查更新。发行版本以新包 `build-info.json` 为准，不能复用旧候选 ZIP 或只改文件名。发布前验证正式包、共享运行库和上传后的三个附件；保持旧个人包与原安装目录不变，不代替用户点击安装。

测试入口为“设置 → 关于 → 检查更新”：发现更高版本后下载，校验完成出现提示卡，再由用户确认“更新并重启”。自动检查有每日周期，手动检查可立即触发；GitHub 限流仍遵守服务器要求。旧的 `261018-1514` 等无更新器版本需先手动升级，不具备此链路。真实桌面升级结果等待用户反馈，不能以新 Release 发布成功视为验收通过。

## 卡片弹出菜单深色背景补充修复

设备/播放来源的默认 WinUI MenuFlyoutPresenter 使用透明填充与独立 Acrylic，内容 RequestedTheme 不足以保证背景匹配。卡片菜单现在使用继承原生模板的专用样式：关闭该菜单的独立 SystemBackdrop，背景、前景、边框用主题资源；卡片本身的 Mica 保留。

回归改为实际展开输出、输入和播放来源菜单，测试浅色→深色→浅色→系统及正常/悬停/按下状态，检查不透明背景、文字和勾选图标，并输出展开菜单图。背景分别为 #F3F3F3 / #202020；测试使用模拟设备，不切换真实设备。弹窗和宿主均检查位于屏幕外，未激活或抢占前台。该 SDK 的 IsConstrainedToRootBounds 在请求约束后仍报告 false，因此验证实际 HWND 边界，不以该标记代替验证。

## 设置与卡片主题同步

设置主题选择现在同时预览设置、流量/设备/媒体卡片和弹出菜单，保存沿用“应用更改”；“重新读取”恢复后台保存的主题。后台旧媒体轮询不能覆盖当前预览，首次打开消息携带已保存主题并在 Show 前应用。次要说明文字改用 WinUI 主题资源，避免透明度叠加造成浅色模式低对比度。

隔离检查从真实主题下拉框执行浅色→深色→浅色→跟随系统，验证三张卡片继承、旧回复不会回退、菜单样式更新，以及未完成设置草稿和抽屉不丢失。文字对比度按中立主题背景核对，默认样本最低约 5.99:1；这是控件颜色检查，不是对用户壁纸上所有 Mica 像素的测量。内嵌歌词既有主题切换检查继续通过。离屏无法取得系统材质配置时明确标记 unavailable-offscreen，实际 Mica/高对比度系统切换与桌面交互由用户验收。

## 包体精简与轻量分发（261018-1408）

移除完整字体、WPF-UI 和未使用的 WinAppSDK 组件，语言资源保留简体中文和英文。当前轻量分发改用系统共享 .NET 8 Desktop/Core 与 Windows App Runtime 2.5.1 x64，不再打包私有运行库；保留实际使用的 WPF、WinForms、WinUI、WebView2、SQLite 与硬件功能。隔离探针已确认 Core/Desktop 来自系统 dotnet/shared，WinUI 来自 WindowsApps 的 x64 运行库包，设置与三张卡片成功加载且未激活窗口。缺失 .NET 路径、不可用框架版本、损坏配置、缺失 WinUI 引导文件和不可用 WinUI 版本检查通过，进程环境恢复；未卸载运行库、修改系统设置、启动正常产品或操作用户音频设备。原生入口缺失依赖提示框、管理员启动和真实桌面交互仍需用户验收。用户已接受 261018-1408，并授权提交、推送及发布其干净 ZIP；这不替代未执行的全部桌面场景验收。发布沿用已验证程序文件，仅重建提交来源清单和分发说明。个人 build 不上传。详见[包体精简说明](docs/包体精简.md)。

## 界面、媒体交互及双份交付

设置精简补充：歌词五个页面合并为一个导航分类；音频页及全局快捷键实现、读写接口删除，通信设备同步与面板固定启用。旧 JSON 中的关闭选项与快捷键不会恢复功能，保存时移除旧字段。屏外实际控件验证了六个主分类、歌词子页导航、应用图标解码、34.4 DIP来源按钮、列表图标及无效图标回退；真实多播放器会话切换仍需桌面验收。Claude Code 的两项只读复核已用于检查快捷键清理与来源图标异步路径。

本轮完整硬件版构建和定向回归覆盖：动态明暗主题切换保留未完成输入及抽屉；真实内嵌歌词 HTML/JS 在 WinUI WebView2 中加载、单双行、自适应前景、隐藏丢帧和脚本执行中重开换曲；设备切换/静音读回和旧回复丢弃；热力图圆角遮罩与无实心矩形的发光结构。模板探针只显示屏外、不激活窗口、不启动 Native/Media 宿主，不调用真实音频设备。

自启动回归使用内存任务定义，验证当前用户身份、最高权限、交互令牌、登录延迟、目标路径、工作目录及错误配置；没有注册、删除或执行真实启动任务。原生共享列布局、WARP 连续 ResizeBuffers 与显示淡入淡出回归通过。便携路径和稳定配置快照检查通过；SQLite 在线备份验证包含 WAL 中已提交记录且不改源数据。

**桌面待验收**：媒体模式真实鼠标入口、浏览器按钮、混合 DPI、真实播放器逐词动画、实际 Mica/Composition 泛光与下次登录启动。屏外 PostMessage 未被 WinUI 提升为实际 PointerPressed，不能把它记为拖动通过；RenderTargetBitmap 不捕获独立 Composition 发光层，因此图片只验证填充/方格布局，圆角泛光还有桌面视觉验收边界。

个人 build 与干净 release 来自同一构建 `261018-1213`。个人包从指定运行实例和媒体共享目录取稳定快照，媒体设置及歌词数据随包读写；release 从未混入个人数据的产物独立组装。用户于 2026-10-18 接受该版本交付，并授权把两轮变更一起提交、推送和发布新 Release；这不等于上列桌面场景全部验收通过。公开 ZIP 在提交后重新生成来源清单，程序二进制与已验证构建一致。发布不自动启动正常产品，不覆盖当前安装；个人包不上传。

## 天气修复发布 261016-1818

`261016-1818` 修复城市名称与代码没有关联、保存后不热加载、没有刷新反馈的问题。硬件页新增天气来源选择、城市搜索结果、只读代码、保存并刷新和即时状态；和风增加专属 API Host 与只写 API Key 设置。原生请求在单一后台队列执行，保持任务栏读取不等待网络，失败不冒充刷新成功，保留旧快照时标明旧城市。

原生隔离测试实际执行“中国天气网 HTTPS 南京搜索 → 保存至测试 INI → 刷新”，找到 `101190101` 并得到有效温度；没有操作用户配置。原生回归覆盖配置保留、错误响应、旧城市快照、状态非阻塞、刷新合并和密钥主机限制。正式包 WinUI 检查覆盖搜索／选城／保存、迟到结果丢弃、密钥只写／留空保留／显式清除、错误恢复、状态轮询和保存互斥；生产内置模块初始化检查通过。和风仅验证模拟响应、专属 Host 与请求头行为，没有使用真实个人 API Key 进行在线验收。

上一构建 `20261016-160645` 的配置与历史已核对哈希、备份迁移至新候选，旧本地目录按授权清理；备份保留在本机 `artifacts/backups/`。用户另行授权提交、推送此次天气修复并创建新 Release；使用实际构建版本 `261016-1818`，不覆盖首发版本，不自动启动正常产品。发布 ZIP 明确排除 WeatherPro、Battery、PowerMon 的个人 INI，以及其他配置、历史和日志。

## 首发记录

首个发布构建为 `261016-1606`（Asia/Taipei），基于已确认可用的功能版本接入新应用图标和统一时间版本号。原生程序、媒体服务与 WinUI 的产品版本一致。正式包模板检查通过；新增歌词库源码构建与简繁转换、QRC/KRC 解码回归通过，依赖图及新输出不含 `ChineseConverter.dll`。首发 ZIP 使用单独的干净文件清单，不包含本机配置、历史、测试图、日志或数据库；原有桌面验收边界仍适用。

首个发布采用用户以“这版先这样”接受的 `20261016-153209` 功能基线；这表示接受当前版本交付，不表示下列桌面项目全部通过。发布版本按构建时刻生成 `yymmdd-hhmm`，最终包信息以随包 `build-info.json` 为准。

该功能基线完成完整硬件 Release 构建、原生几何／连续尺寸调整回归、热力图模型与设置回归，以及正式包模板检查。用户已确认能拖动模块并应用，本轮没有改动拖动事件处理。原生任务栏与预览改为双行共享列宽；正式包测量覆盖上下列边界、独占双行、空缺尾项，以及字号／间距／数值对齐的即时编辑。离屏鼠标消息依然不能替代真实鼠标验证。

浅色／深色热力图均已检查实际控件图：84格、21DIP方格、内容240DIP，位于252DIP可用高度内，无滚动条。125%缩放实测方格20.8×20.8DIP，属于物理像素取整。模型覆盖相对日峰值分级、峰值变化、全零／未知和未来日期。截图为模拟数据，系统Mica与独立Composition柔光不在截图验收范围。

本地迁移检查已将上一候选的已保存配置、历史和三个内置模块 INI 备份迁入并核对哈希；确认无占用后按授权清理旧包。备份留在本机 `artifacts/backups/`，不随仓库或发布 ZIP 分发。检查没有停止用户程序、改变音频设备或启动新主程序。

## 当前实现

- 原生任务栏保留硬件／网络监测和歌词／媒体自动切换。
- 设置与流量、设备、媒体三卡使用 WinUI3；模块池、单／双条组合及右侧抽屉已接入真实布局。
- 音频设备使用浮动列表；共用字体／背景／透明度集中配置。
- 电池、功耗、天气源码内置；保留配置和历史迁移，删除外部插件页、主窗口皮肤和阈值通知。
- Windows 设置式分组与响应式字段；共用外观使用字体／字号和 B/I/U/删除线按钮。顶部预览具有对比底板；设置窗口沿用主程序图标。
- 播放源使用六点柄列表，模块和来源共用窗口内指针捕获，保留键盘与菜单操作。
- 双行上下对应项目共用列宽和间距，保留拖动顺序与所在行。热力图参考 token-monitor 的蓝色色阶与柔光，按最近12周日峰值分级，显示峰值和选中日期用量。

## 已有验证证据

| 范围 | 已完成检查 | 不能据此认定的事项 |
| --- | --- | --- |
| 独立构建 | 完整硬件 Release x64、媒体服务与共享运行库 WinUI 发布成功；项目输入边界检查 | 不等同于桌面验收 |
| 服务与状态 | 模式、管道重连／容量／取消、设置持久化、歌词与历史场景回归 | 不替代真实播放器和设备操作 |
| 内置功能初始化 | 实际原生目标文件构造、资源读取和原显示项 ID 检查 | 天气在线服务与硬件读数仍待实机确认 |
| 连续调整尺寸 | 使用真实 DXGI／D2D 的 WARP 回归，复现缓存目标未解绑的失败并确认修正后连续调整成功 | 不覆盖所有显卡／覆盖层组合 |
| 模块编辑 | 模块池与独立行、完整提交、保存期间新草稿保留；用户确认基本拖动与应用 | 边缘滚动、取消等全部鼠标边界场景尚未逐项验收 |
| WinUI组件 | 正式程序诊断入口加载设置页与抽屉，检查关闭复用和重复卡片物理定位 | 隐藏内容零尺寸明确标为不可测量 |
| 正式包资源 | PRI/XBF存在且与本次生成文件哈希一致 | 不能单凭存在性判断完整界面行为 |
| 正式包模板 | 屏幕外 `Show(false)`，不激活、不请求焦点；设置与四张普通卡均 `IsLoaded=true` 且内容宽度非零，更新卡另有专门检查 | 不覆盖全部桌面交互和视觉效果 |
| 卡片生命周期 | 实际 HWND 首次显示、动画隐藏后立即重开、隐藏期间销毁；外部点击分类覆盖卡片、浮动列表、左右键入口 | 分类使用捕获记录作为输入；真实桌面钩子交互仍待验收 |
| 音频交互 | 真实 WinUI 静音按钮、模拟管道读回、保持音量、单侧锁定与迟到状态拒绝 | 不代表真实声卡的静音效果或设备切换耗时 |
| 双行对齐 | 真实原生布局函数验证不等宽列／行数量／DPI；正式包预览验证同列X及宽度与编辑响应 | 最终任务栏外观待用户查看 |
| 热力图 | 相对峰值模型回归，实际浅／深色控件截图、方格尺寸、容量与日期选择检查 | 不替代DWM材质／柔光的桌面效果 |
| 设置实际布局 | 正式包加载模拟配置，验证预览、复选框模板间距、宽窄分列、等宽来源行；渲染图复核 | 不包含 DWM Mica，也不代表物理鼠标已验收 |
| Mica 材质 | 用户认可当前观感；同位置实屏对比，控制器参数、主题、系统默认、回退恢复与释放检查通过 | 不代表其他壁纸、混合 DPI 或全部系统策略场景均已验收 |
| 完整更新包 | GitHub 下载 ZIP 经旧版实际解压／暂存复核；完整新旧清单经原生隔离预检 | 尚不等于日用安装已完成真实升级重启 |

## 已修复的关键故障

**拖动后双行不再对齐。** 保存显式行号后曾切换到各行分别累加宽度的分支，与旧布局的上下配对列规则不同。现在同列占位取上下项目所需宽度的较大值，统一累加间距，保留用户顺序与行号；普通单侧剩余项留在所选半行，明确独占双行的项目占完整列。旧命中矩形也随重排清空。

**2026-10-16 卡片自动关闭。** 新日志确认前台仍为原生宿主时，被 `focus-lost` 路径约半秒后反复收起；之前增加宽限时间没有解决根因。现已删除焦点收起机制，只处理实际外部点击、Esc、入口切换和进入设置。鼠标监听仅在显示期间存在，不吞输入；入口 HWND 经宿主 PID 校验。有界 `UI/card-lifecycle.log` 记录点击分类与收起原因，进程异常另记 `UI/ui-error.log`。

**音频切换与静音。** 静音独立写入并核对读回；不再先枚举全部设备或附带音量写入。缓存设备和订阅，直接应用操作回复。音频与其他请求分别有界排队。计时记录区分排队、系统调用和 UI 应用，不能据静态优化承诺所有驱动的耗时。驱动不能读取音量时保留可选择的设备，并明确禁用不可用的音量／静音控件。

**2026-10-16 拖动与控件排布。** 普通 Button 的指针类处理与根层捕获冲突，且后代捕获丢失事件曾取消根层拖动；现统一由根层持有手势。复选框不再清零原生内边距。透明背景的白色预览文字增加对比底板，避免在浅色设置页面中不可见。

拖动补充：捕获在按钮释放时丢失会等待释放事件或以按钮已抬起的位置结束一次；真正抢走捕获、Esc 或窗口失活取消。加入浮影、插入线和边缘滚动，`UI/ui-gestures.log` 保留有界事件记录。基本拖动与应用已有用户确认；其余鼠标边界路径仍待逐项验收，算法或编译检查不作为这些路径的完成证据。

**WinUI设置／卡片闪退。** 正式发布曾遗漏 `TaskBarWidgets.UI.pri` 和 `App.xbf`，项目引用形式的验证宿主带入资源，掩盖了发布缺失。现已将本项目生成资源加入发布清单并增加校验；隔离副本去掉资源可复现相同 `0xC000027B`，完整包的真实模板加载通过。无参数启动不能触发这条路径，不作为修复证据。

**程序入口前崩溃。** 内置功耗模块的全局对象在 MFC 准备前构造。三个内置模块及数据管理器改为初始化后首次使用时构造，资源明确从主程序读取。

**连续排序后 Direct2D 错误。** 缓存仍间接持有交换链目标，导致 `ResizeBuffers` 失败。调整尺寸前显式解除 D2D 目标绑定；实际图形对象回归通过。

**旧卡片宽度及二次重叠。** WPF隐式最小宽度与两套尺寸管理曾造成差异；旧WPF卡片现已删除，WinUI卡片统一用AppWindow管理几何尺寸。

## 待用户桌面验收

1. QQ／网易云播放、暂停、换曲以及浏览器并存时的自动／手动切换，左键、右键和滚轮。
2. 基本模块拖动与应用已由用户确认；本轮待查看新的上下列对齐、间距、占位，以及关闭／重开后的保持。
3. 设备浮动列表不关闭卡片组、不改变卡片尺寸，输入输出音量和静音可操作。
4. 混合 DPI、多屏和 Explorer 重启后的材质、阴影、泛光与动画，以及系统高对比度／全局透明效果切换；当前环境下的 Mica 观感已由用户认可。
5. 原始配置、内置功能设置重启生效、天气及功耗读数。

NVIDIA游戏提示仍未确认有可靠的应用侧关闭方式，未修改全局覆盖层设置。日志中出现过游戏加加注入模块，只能说明它介入过进程，不能认定它是崩溃原因。

## 交付规则

构建不自动启动主程序。用户数据先备份并核对，再迁入新候选；用户已允许清理紧邻上一版，遇到运行进程、锁定驱动或不安全路径则保留。原日用安装与三个参考项目不修改。

2026-10-16 用户已授权本轮文档／仓库清理、提交、推送与首个 GitHub Release ZIP；参考项目仅作为本地忽略的只读目录，不随仓库发布，产品构建不依赖它们。发布 ZIP 应从干净构建产物生成，排除本地配置、历史、天气凭据、备份与检查日志／图片，并保留 Anti 996 和第三方许可声明。

检查命令见[产品说明](README.md)。不自动启动正常产品，不把测试数量或离屏截图作为全部桌面验收结论。

## 2026-10-21 原生媒体渲染改造

任务栏媒体、歌词与频谱改由原生 DirectComposition 渲染，歌词卡片改由 WinUI 原生文本行渲染；媒体服务只发送 `renderVersion:2` 结构化快照，不再传输 HTML、CSS 或脚本，也不再提供网页后备路径。

**源码与依赖。** Media 不再引用 `Microsoft.Web.WebView2`；删除 `Media/Web/**`、WebView 消息路由与脚本工厂、旧窗口辅助类、Web 测试工程、对应许可清单条目与打包／探针脚本中的旧资源引用。原生工程不含 WebView2 头文件或 Loader 链接，产品 XAML 不创建 WebView2 控件。Windows App SDK WinUI 仍以传递依赖带入 WebView2 程序集（UI 目录中的 `Microsoft.Web.WebView2.Core.dll` 等）；未在真实运行中证明其不被加载前，不擅自裁剪框架依赖。

**本轮修正。**
- 任务栏与卡片分别推导展示场景：任务栏显示频谱或切回监测时，可见卡片仍按真实歌词时间换行；关闭卡片后停止快速歌词调度。
- 逐词扫描新目标首帧从起点开始，完整序列不再先到终点再回退；相同目标不重启插值；Seek、换行、暂停与恢复仍立即校正。
- 隐藏后重显同时失效插值器与输入缓存，同一 revision／进度重显即恢复高亮；窗口重新绑定路径同样复用保留快照。
- 减少动画模式始终显示权威扫描目标，不再从未初始化的插值器回读；运行中切换模式会重建扫描状态。

**验证。** `tests/Media.Tests` 275 项、`tests/Lyrics.Core.Tests` 27 项通过，其中新增界面场景测试覆盖频谱＋卡片、任务栏切回监测、关闭卡片停更与真实歌词换行。`Native/tests/run-native-tests.ps1` 全部通过，新增断言检查新目标到达时刻、中间时刻与结束时刻，以及暂停／词间停顿重显、重复快照、宿主重绑和减少动画连续绘制。完整硬件 Release 构建（原生、Media、UI、独立更新器、共享运行库）通过；`test-ui-templates.ps1` 离屏加载设置与四张卡片且 `webView2Used=false`；`test-shared-runtime.ps1` 确认 Media 从系统共享运行库加载且无窗口设置服务可用；`run-managed-runtime-test.ps1` 的共享运行时、缺失／版本／配置失败与子进程启动检查通过。更新健康与更新器隔离检查见本轮交付说明。

**初始改造时未执行。** 当时尚未进行正常产品启动、真实播放器与音频、Explorer 重启、多显示器混合 DPI、真实桌面视觉与输入验收。后续实际安装暴露的故障及补充验收见下文；离线测试与离屏模板不能替代这些场景。

## 2026-10-21 媒体管道与封面回归修复

`261021-2047` 安装后出现系统监测仍刷新、媒体不显示、卡片和设置无法打开。安装文件清单一致，但原生管道反复解析失败并重连；该版本已撤回为草稿。

**两处管道错误。** OVERLAPPED 读取同步完成时，原实现先处理一次字节，又消费同一次完成事件，造成分片 JSON 重复拼接。提取生产使用的 `NativePipeReader`，同步和挂起完成均只交付一次；取消读取后确认完成再释放资源。后续 `261021-2146` 仍有命令唤醒后的故障：`Pump()` 把尚未完成的读取判成失败。现改为继续等待原读取，同时移除自动重置唤醒事件的重复 `ResetEvent`，避免丢失新命令的唤醒；日志保留清理前的原始错误码。

**封面图层。** 普通重绘曾把封面画入隐藏备用层，切换和重显时又可能让两个图层同时可见。现在显式选择实际绘制图层，同图重绘使用当前层，新图使用备用层，切换完成及重显后隐藏旧层；帧更新不再打断正在进行的封面淡入淡出。

**回归覆盖。** 原生传输测试覆盖同步／挂起完成、UTF-8 与换行分片、消息上限、取消、重连、连续命令唤醒和超时后恢复；旧算法取证用例保留重复拼接证据。跨语言测试连接真实 `MediaPipeServer` 与生产 reader。封面测试使用不激活的离屏窗口创建真实 DirectComposition 图层，检查首次绘制和交换图层后重显时实际活动层已绘制；该检查不冒充屏幕像素验收。

**实际桌面确认。** 用户授权备份个人数据并升级日常安装后，确认 `261021-2159` 的卡片、设置与任务栏媒体全部恢复；日志显示单次连接、设置请求成功，无原有断线循环。`261021-2219` 又经用户确认封面与频谱正常。频谱独立采集及运行中诊断均收到非零信号，原生绘制日志确认消费非零频谱；本轮没有据此推断音频采集算法存在已修复缺陷。个人配置与历史在文件替换前后通过哈希校验。

**保留的边界。** 上述确认限于用户本机及本次操作。Explorer 重建、多屏混合 DPI、缺少 WebView2 Runtime 的环境及所有播放器组合未完成全面验收。更新健康标记仅证明启动握手成功，不能代替完整媒体帧、持续连接和交互验证。公开 Release 说明仅包含功能变化、优化和修复；验证证据记录在本文和本地产物中。

## 2026-10-22 DPI 变化后的交换链恢复

`261021-2224` 在任务栏 DPI 由 120 调整到 144 后持续记录
`DXGI_ERROR_INVALID_CALL (0x887A0001)`。设备重建路径在覆盖旧 D2D context 前
没有解除其交换链 target；全局位图与图标缓存仍持有旧 context，使
`ResizeBuffers` 每帧失败。

**修正。** `CD2D1DeviceContextHelper::Reinitialize()` 在创建新 context 前先
调用 `Reset()`。若交换链仍因其他引用无法原地调整，重新创建交换链并重新绑定
合成 visual，同时保留首次错误日志。源码守卫检查解绑顺序与恢复分支。

**验证。** 完整 Windows Release 构建通过。原生回归重新运行并通过：缓存旧
context 下连续四次调整尺寸、修复前顺序复现失败、解除 target 后恢复、交换链
重建后继续调整，以及原有合成淡入淡出检查。另含既有数值、布局、媒体离屏、
渲染生命周期和原生管道全套测试；仅保留 `PluginInterface.h` 的既有 C4100
未使用参数警告。

**待桌面验收。** 真实 Explorer 重建、混合 DPI 多显示器、分辨率／刷新率变化、
RDP／锁屏恢复，以及交换链重建时的实际合成器画面和长期日志稳定性，仍需在
安装后的桌面环境确认；WARP 回归不能替代这些场景。
