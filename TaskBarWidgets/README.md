# TaskBarWidgets

Windows 11 任务栏工具，使用 WinUI 3 设置与快捷卡片，整合硬件与网络监测、同步歌词、媒体控制和输入输出音频设备控制。以 TrafficMonitor 任务栏与布局为基础，复用 TaskbarLyrics 歌词效果及 AF-Media-Bar 音频控制。左键面板从上到下为流量、设备、媒体三个等宽独立卡片。电池、功耗、天气直接编入程序，不扫描外部插件 DLL；选项放在硬件页。开发状态与待验收项目见 [验收记录](ACCEPTANCE.md)。

发布 ZIP 从仓库的 [Releases](https://github.com/NoctisWang528/TaskBarWidget/releases) 下载；版本使用构建时刻的 `yymmdd-hhmm`。完整解压到可写目录，保留 `Media/`、`UI/`、`licenses/` 等子目录，再启动根目录的 `TaskBarWidgets.exe`。较早版本首次升级需先退出旧程序，备份并迁移配置和历史，再使用含更新器的完整新包；不要手工覆盖正在运行的目录。后续支持下述应用内更新。当前桌面验证范围与已知限制见 [ACCEPTANCE.md](ACCEPTANCE.md)。

所有产品源码与构建依赖均位于此目录。开发工作区可保留 `TrafficMonitor/`、`TaskbarLyrics/`、`AF-Media-Bar/` 三个只读参考目录；它们不跟踪、不随 GitHub 仓库或发布包分发，构建也不读取它们。

## 应用更新

`261018-1638` / `261018-1651` 的旧更新器存在 Windows 路径分隔符比较问题，可能报告 “Process ID no longer belongs to this installation”。请先应用一次修复更新器，或手动升级到修复后的完整发行包；新版内置修复，升级后无需再次替换更新器。失败发生在退出与替换文件前。

“设置 → 关于”提供检查更新、下载／取消下载、版本、进度与更新说明。默认开启“自动检查并下载更新”，选项立即保存，不需要点击“应用更改”。后台仅使用本仓库的正式 GitHub Release，启动后延迟检查，并按已保存的检查时间、每日周期及服务端限流安排后续检查。草稿、预发布、缺少协议附件或不兼容版本会提示手动更新。

校验完成后，下次打开卡片组会在流量卡片上方增加约 72 DIP 高、同宽同主题的更新提示卡。更新准备好时不会自动弹出卡片或发送系统通知。关于页与提示卡使用同一个安装确认流程；如果设置有草稿、未保存天气密钥／城市选择或天气操作尚未完成，会先提示处理，不自动保存或放弃。确认“安装并重启”后才启动实际替换。

根目录的 `TaskBarWidgets.Updater.exe` 是独立原生更新器。安装时先复制到安装目录外的 `%LOCALAPPDATA%/TaskBarWidgets/Updates/<安装路径哈希>/jobs/<作业 ID>/`，完成预检并取得主程序关闭批准，等待 Native、Media、UI 正常退出；随后备份程序、配置与数据库，在原安装目录更新受管程序文件并重启。开机启动路径保持原目录。不会自动安装 .NET、Windows App Runtime 或 WebView2；缺少依赖时按提示进入官方页面安装后重试。

新版本需要在 60 秒内完成原生初始化及 UI 就绪报告。失败时先请求新进程正常退出，再恢复本次备份并启动旧版本；遇到占用或不安全状态会保留作业和备份。断电或中断后若启动提示需要恢复，请保留整个作业目录，在应用已关闭后，用同一 Windows 用户运行其中的 `recover.cmd`（必要时使用与原程序相同的管理员权限）。它执行该目录内更新器的 `--recover job.json`；不要编辑作业文件或删除未完成备份。当前版本尚无更新器的旧安装不能通过这条链路自行升级，需要先完成一次手动升级。

完整格式、状态与发布附件见[更新协议](docs/更新协议.md)。下载／校验和离屏检查不能代替真实 Explorer、管理员权限、退出重启及回滚的桌面验收。

## 构建

需要 Windows、Visual Studio 2022 Build Tools（v143、MFC、C++/CLI、Windows SDK、.NET Framework 4.7.2 targeting pack）、.NET 8 SDK。网页测试需要 Node.js 与 npm。缺少 Visual Studio 组件时可导入 [.vsconfig](.vsconfig)，或运行 `scripts/install-build-components.ps1` 并确认 Windows 管理员提示。

从本目录运行：

```powershell
.\scripts\build.ps1
.\scripts\verify.ps1
```

默认构建始终创建新目录，同秒目录冲突会报错，不覆盖已有候选。单独增量更新某个候选的组件时，可明确指定输出目录：

```powershell
.\scripts\build.ps1 -Component Media -OutputDirectory (Get-Content .\artifacts\latest-build.txt)
```

构建和单元测试不会自动启动产品。明确要求不启动时，不运行两个 smoke 启动脚本。

允许桌面启动验收时，主程序启动或打包发生变化后可运行 `.\scripts\smoke-native.ps1`。此检查会请求管理员权限，确认主进程持续运行且任务栏窗口已创建，并保留程序运行；它与仅测试辅助程序的 `smoke-media.ps1` 分别覆盖两个入口。

完整构建使用硬件监测版本，不使用 Lite 替代。依赖缓存写入 `.packages/`，日志和最终程序写入 `artifacts/`。启动入口为最新构建目录中的 `TaskBarWidgets.exe`，其 `Media/` 与 `UI/` 子目录必须保留。smoke 与导入脚本默认读取 `artifacts/latest-build.txt`；没有有效记录时需先完整构建，或明确指定目录（smoke 使用 `-OutputDirectory`，导入使用 `-DestinationDirectory`）。只有成功的完整构建更新该指针。

完整监测程序沿用上游的管理员运行要求，以保留温度等硬件访问能力；启动时可能出现 UAC。媒体服务和 WinUI 界面由主程序启动，不要单独双击两个辅助 EXE。运行前需安装 .NET 8 Desktop Runtime x64、Windows App Runtime 2.5.1 x64 或兼容的更新 2.x，下载链接和安装选择见[运行环境要求](../README.md#下载与运行)。不需要开发 SDK。另需 Microsoft Edge WebView2 Runtime；缺失时媒体视图不可用，监测视图仍可回退。

构建默认输出到 `artifacts/builds/yyyyMMdd-HHmmss/`（构建开始时的本地时间）。`artifacts/latest-build.txt` 记录最近成功的完整构建，包内 `build-info.json` 记录构建信息。构建和测试不会自动启动正常产品，也不会替换日用安装或设置自启。

## 日用迁移

默认使用独立的 TaskBarWidgets 配置与缓存。先退出现有占用同一区域的任务栏工具，再启动新程序。保留旧安装作为回退；不要让两套应用同时写入相同配置。

从源码工作区导入现有布局时，先退出新程序，再从本目录运行以下命令并输入旧安装目录。脚本只读取旧安装，将配置副本写入最近完整构建目录。导入目标必须位于本工作区的 `artifacts/` 下；需要导入发布 ZIP 时，先解压到该目录，再用 `-DestinationDirectory` 指定目标。已有新配置会先备份。内置功能沿用 `plugins/` 下三个 INI 文件作为配置兼容目录；该目录中的 DLL 不再加载。

```powershell
$oldInstallation = Read-Host '旧 TrafficMonitor 安装目录'
.\scripts\import-monitor-settings.ps1 -SourceDirectory $oldInstallation -IncludeBuiltinSettings
```

桌面主窗口与其皮肤系统不属于新产品功能；任务栏字体、颜色和可定制布局仍保留。

历史流量可单独导入到关闭状态的目标目录；以下命令沿用上面的旧安装目录变量：

```powershell
.\scripts\import-traffic-history.ps1 -SourceFile (Join-Path $oldInstallation 'history_traffic.dat')
```

导入前备份两份历史，同一天保守地保留合计较大的完整记录，不重复累加。日历统计上传与下载之和，只代表程序监测期间记录的流量；空框表示无记录，灰格表示已记录的零流量。蓝色色阶依据最近12周的日峰值分级，图例显示当前峰值；不同窗口的颜色不能直接比较绝对用量。点击日期查看当天合计。

快捷面板与“同时设为默认通信设备”固定启用，只在主动选择设备时更改默认设备；不再提供音频偏好页或全局快捷键。设置按钮位于媒体卡片右上角。

`261018-1213` 起，便携模式的媒体数据随程序放在 `Data/`。从较早版本手动升级时，先退出旧版并备份，再将旧目录的 `config.ini`、`global_cfg.ini`、`history_traffic.dat` 及 `plugins/` 中三个内置模块 INI 复制到新目录；将 `%APPDATA%/TaskBarWidgets/media-settings.json` 和 `database/` 分别复制到新目录的 `Data/media-settings.json` 与 `Data/database/`。如果旧目录已存在 `Data/`，优先完整复制该目录，不用旧共享数据覆盖它。非便携模式仍使用原 AppData 位置。

GitHub 的干净 ZIP 不带任何个人配置或历史。本地个人 build 已完成数据快照迁移，不能作为公开附件上传；双份包制作详见[便携与分发包](docs/便携与分发包.md)。

登录自启统一使用当前用户的交互式最高权限计划任务。首次使用新目录，在“设置 → 常规”查看登记状态，按需“修复并启用登录启动”；程序不会在打包或仅打开设置时自动修改启动项。

产品及新增整合代码沿用 [Anti 996 许可](licenses/TrafficMonitor-LICENSE)。第三方代码保留各自的 MIT、MPL、OFL 等许可；来源和完整声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 及 `licenses/`。


## WinUI 界面与布局

### 配置天气城市

进入“设置 → 硬件与网络 → 天气”，选择来源后输入城市或区县名称并点击“搜索”（或按 Enter），从结果中选择城市，再点击“保存城市并刷新”。城市代码由搜索结果提供，不能仅通过改显示名称来切换城市。以中国天气网的南京为例，城市代码为 `101190101`；该来源不需要 API Key。

和风天气需要先填写控制台分配的专属 **API Host** 和 **API Key**，点击“保存连接配置”后再搜索城市。Host 支持纯域名或 `https://` 前缀，仅允许和风专属 `*.qweatherapi.com` 域名；不再使用停用中的公共地址。密钥不会读回界面，留空保留原值，“清除已保存密钥”才会删除。本机配置沿用 `WeatherPro.ini`，不要把此文件或密钥放入截图、仓库或共享压缩包。参见[和风 API Host](https://dev.qweather.com/docs/configuration/api-host/)和[认证说明](https://dev.qweather.com/docs/configuration/authentication/)。

保存城市立即生效，无需重启。“立即刷新已保存城市”不提交尚未选择／保存的搜索文字。页面显示最近尝试与成功时间、错误原因；网络失败会保留成功数据并说明其所属城市。其他天气显示选项点击上方“应用更改”后自动重新加载和刷新。

设置用模块池和模块组合条定义显示内容；水平单行打开时为一条，关闭时为上／下两条。双行的上下项目共用列宽，各列按较宽项目预留空间，间距在列之间统一计算。拖入启用、拖回移除、条内或跨条移动；点击模块在右侧抽屉编辑属性。顶部预览用示例读数展示布局，透明背景使用对比底板。共用外观使用字体／字号与 B、I、U、删除线工具条；位置和监测选项随内容宽度分成一或两列。播放源通过左侧六点柄拖动排序，也支持 Alt + 上下方向键和右侧菜单。设备列表采用浮动菜单，不改变卡片尺寸。旧 WPF 卡片已删除，媒体服务继续复用原有核心逻辑。

`dotnet run --project UI.Tests/TaskBarWidgets.UI.Tests.csproj -c Release` 检查前端管道，`scripts/validate-winui.ps1` 使用正式发布程序的离屏诊断入口检查 WinUI 初始化和卡片物理尺寸。隐藏窗口不能证明完整视觉布局、输入或材质已验收。


## 发布包检查

```powershell
# 检查独立发布确实包含本项目的 PRI/XBF，避免验证目录补齐资源掩盖漏包
.\scripts\test-winui-publish.ps1
# 对最近完整发布包加载设置与三卡模板：屏幕外、不激活，不启动主程序或媒体服务
.\scripts\test-ui-templates.ps1
```

`test-ui-templates.ps1` 要求正式包中的应用 PRI 与 App.xbf 存在；每次发布也会校验其与本次生成资源的哈希。它使用模拟配置检查宽窄布局、预览和复选框间距，将不含系统 Mica 的控件图写入包内 `ui-layout-check/`，并验证卡片隐藏／重开／销毁竞态。构造成功或无参数启动成功都不能替代模板加载检查。

`scripts/package-release.ps1` 从完整构建制作干净 ZIP、`SHA256SUMS.txt` 和 `update.json`，ZIP 内含逐文件 `app-files.json`。正式上传必须使用已提交的源代码状态，保留第三方许可证并排除个人配置、历史和检查产物。发布先创建 draft、上传全部附件，再下载核对大小、ZIP SHA-256 与清单哈希，确认一致后才公开为正式 Release；缺少更新元数据的附件不能供应用内安装。`-AllowDirtyLocal` 仅用于明确标记的本地交付，不作为正式发布替代。具体步骤见[更新协议的发布流程](docs/更新协议.md#发布流程)。

上一版本的清理使用 `scripts/retire-previous-build.ps1 -PreviousBuild <上一构建目录>` 预览；显式 `-Execute` 才备份、迁移并删除。最新状态见 [ACCEPTANCE.md](ACCEPTANCE.md)。

## 包体精简与私有运行库

轻量版保留现有功能，Media/UI 使用系统共享 .NET 8，WinUI 使用系统安装的 Windows App Runtime 2.5.1 或兼容更新的 2.x；不再打包 Runtime/ 或 WinUI 原生框架 DLL。原生入口先验证实际依赖，失败显示下载入口；不会自动安装。只携带程序所需托管接口、WebView2/WinUI 引导 DLL 和 VC/MFC 文件，字体使用系统字体。

.NET 使用 LatestPatch 策略，选择已安装的兼容 8.0 补丁，不自动跨到 9/10。原生与媒体启动器仅对子进程指定系统 x64 .NET 路径；不修改用户/系统环境。WinUI 最低版本来自与项目一致的 NuGet 版本常量；构建机仍需 SDK，使用者只需 Runtime。

完整解压到新目录并迁移 Data/ 等个人文件；不要覆盖旧自包含目录。`scripts/check-runtime-layout.ps1` 校验无随包运行库与依赖声明；`scripts/test-shared-runtime.ps1`、`Native/tests/run-managed-runtime-test.ps1` 及实际发布的 WinUI 探针验证共享加载和缺失依赖分支，不启动正常产品或操作音频设备。
