<p align="center"><img src="TaskBarWidgets/assets/app-icon.png" width="112" alt="TaskBarWidgets 图标"></p>

# TaskBarWidgets

把系统监测、歌词和音频控制放进 Windows 11 任务栏。

音乐播放时显示歌词，暂停后回到系统状态；单击打开流量、音频设备和媒体卡片。通过拖动模块，自定义任务栏显示内容和单／双行布局。

[下载最新版本](https://github.com/NoctisWang528/TaskBarWidget/releases/latest) · [使用与构建说明](TaskBarWidgets/README.md) · [验证与已知限制](TaskBarWidgets/ACCEPTANCE.md)

## 功能

- **任务栏监测**：网速、CPU、内存、GPU、温度等；电池、功耗与天气功能已内置。
- **歌词与媒体**：QQ 音乐、网易云音乐播放时显示歌词；浏览器媒体显示曲目信息与控制。滚轮可临时切换显示模式。
- **可拖动布局**：模块池、单／双行组合、右侧属性抽屉和预览；双行上下共用列宽，保留对齐。
- **音频设备**：切换输入／输出设备，分别调整音量和静音；可同时设置默认通信设备。
- **流量日历**：最近 12 周上传与下载合计，按日流量峰值分级，支持日期详情、浅深主题和柔光。
- **WinUI 3 界面**：Mica 设置与三张独立卡片。没有桌面主窗口、皮肤或外部插件管理。

## 效果展示

以下图片由实际 WinUI 控件离屏渲染，使用示例数据，不包含个人账户、播放器封面或设备信息；它们不是桌面实机截图。系统 Mica 和独立 Composition 柔光需运行程序查看。

### 任务栏布局与预览

![模块组合与双行预览](TaskBarWidgets/docs/images/settings-layout.png)

### 字体与外观

![共用外观与响应式设置](TaskBarWidgets/docs/images/settings-appearance.png)

### 流量日历

<p><img src="TaskBarWidgets/docs/images/traffic-light.png" width="46%" alt="浅色流量日历示例"> <img src="TaskBarWidgets/docs/images/traffic-dark.png" width="46%" alt="深色流量日历示例"></p>

## 下载与运行

1. 从 [Releases](https://github.com/NoctisWang528/TaskBarWidget/releases) 下载 `TaskBarWidgets-<版本>-win-x64.zip`。
2. 完整解压到一个可写目录，保留 `Media/`、`UI/`、`language/` 和 `licenses/` 等子目录。
3. 先退出旧版或其他占用同一任务栏区域的监测工具，再运行根目录的 `TaskBarWidgets.exe`。完整硬件监测会请求管理员权限。

系统要求：**Windows 11 x64**，以及 Microsoft Edge WebView2 Runtime。ZIP 自带 .NET、WinUI 和所需 VC 运行文件；不要单独运行两个辅助程序，也不要直接从压缩包里启动。

版本号采用构建时间 **`yymmdd-hhmm`**，时区为 Asia/Taipei。例如 `261016-1705` 表示 2026 年 10 月 16 日 17:05 的构建。Release 提供 ZIP 和 SHA-256 校验文件；不包含开发者配置、流量历史、日志或凭据。

| 操作 | 行为 |
| --- | --- |
| 单击任务栏窗口 | 打开／收起卡片 |
| 右键任务栏窗口 | 打开设置菜单 |
| 鼠标滚轮 | 临时切换监测／媒体模式，下次播放状态或来源变化恢复自动模式 |
| 点击卡片外部、按 Esc | 收起卡片；单纯焦点变化不会收起 |

硬件读数取决于设备和驱动；歌词与在线天气取决于外部服务。首发仅提供 x64 ZIP，不提供安装器、自动更新或数字签名。配置迁移说明见[产品文档](TaskBarWidgets/README.md)。

## 从源码构建

需要 Visual Studio 2022 C++ Build Tools（MFC、C++/CLI、Windows SDK 和 .NET Framework 4.7.2 targeting pack）及 .NET 8 SDK。构建组件参照 `TaskBarWidgets/.vsconfig`。

```powershell
git clone https://github.com/NoctisWang528/TaskBarWidget.git
cd TaskBarWidget
.\TaskBarWidgets\scripts\build.ps1
```

所有产品代码与依赖声明都在 `TaskBarWidgets/`。仓库不再包含三个完整上游项目，构建不需要它们。本地输出、配置、依赖缓存和只读参考副本均不跟踪。脚本默认不启动应用。

- [界面规范](TaskBarWidgets/docs/界面统一方案.md)
- [进程通信协议](TaskBarWidgets/IPC.md)
- [开发规范](AGENTS.md)

## 许可证

本项目沿用 TrafficMonitor 的 **Anti 996 License 1.0**，见 [LICENSE](LICENSE)。第三方组件继续适用各自许可证，不因整合而变更；完整来源与授权文本见 [THIRD-PARTY-NOTICES.md](TaskBarWidgets/THIRD-PARTY-NOTICES.md)。图标通过 ChatGPT 图像生成工具制作，提示词与资产说明见 [assets/README.md](TaskBarWidgets/assets/README.md)。

## 致谢与代码来源

**本项目使用了 TrafficMonitor、TaskbarLyrics、AF-Media-Bar 这三个项目的部分代码。**

- [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)：任务栏窗口、系统／硬件监测及可定制布局。
- [TaskbarLyrics](https://github.com/ANYNC/TaskbarLyrics)：歌词获取、解析与呈现，以及媒体会话功能。
- [AF-Media-Bar](https://github.com/Fervent-Tempo/AF-Media-Bar)：音频设备切换相关实现。

同时感谢内置电池、功耗、天气模块及其他依赖的作者；流量热力图的视觉与相对峰值分级参考了 [token-monitor](https://github.com/Javis603/token-monitor)。保留原作者署名与许可证。本项目是独立整合项目，不代表上游作者提供维护或背书。
