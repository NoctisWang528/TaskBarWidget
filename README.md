# TaskBarWidget

面向 Windows 11 的任务栏工具，整合硬件／网络监测、同步歌词、媒体控制和输入输出音频设备切换。当前产品开发位于 [TaskBarWidgets/](TaskBarWidgets/)，设置与快捷卡片使用 WinUI 3。

## 当前功能

- 保留 TrafficMonitor 任务栏及硬件监测；通过模块池、单／双条组合和属性抽屉配置显示内容。
- QQ／网易云播放时显示歌词，暂停后恢复监测；浏览器播放显示信息与控制，不查询歌词。滚轮临时切换显示模式。
- 三张等宽独立卡片按流量、设备、媒体排列，包含最近12周流量热力图、设备浮动选择列表和媒体控制。
- 电池、功耗、天气直接从源码编入产品；移除桌面主窗口、皮肤、外部插件管理及阈值通知。

项目仍在迭代。最新验证包括完整硬件构建、正式发布包的 WinUI 资源与模板加载检查；桌面操作与视觉效果仍需验收。详情见[验收状态](TaskBarWidgets/ACCEPTANCE.md)。不以测试数量代替实际验收。

## 目录与文档

| 目录 | 用途 |
| --- | --- |
| [TaskBarWidgets/](TaskBarWidgets/) | 独立产品源码、依赖声明、构建脚本与定向检查 |
| [docs/需求与架构.md](docs/需求与架构.md) | 当前需求、架构和开发边界 |
| [TaskBarWidgets/docs/界面统一方案.md](TaskBarWidgets/docs/界面统一方案.md) | 当前界面与交互规范 |
| [TaskBarWidgets/IPC.md](TaskBarWidgets/IPC.md) | 进程通信协议 |
| [docs/整合方案讨论稿.md](docs/整合方案讨论稿.md) | 历史讨论记录，可能包含已被替代的方案 |
| TrafficMonitor/、TaskbarLyrics/、AF-Media-Bar/ | 只读上游参考；产品构建不依赖它们 |

构建入口、运行依赖和迁移步骤见[产品说明](TaskBarWidgets/README.md)。所有本地构建输出、个人配置、日志、缓存及备份均排除在 Git 之外；构建默认不启动程序，也不替换日用安装。

上游来源及各自许可见 [THIRD-PARTY-NOTICES.md](TaskBarWidgets/THIRD-PARTY-NOTICES.md)。贡献前阅读[仓库规范](AGENTS.md)和[产品规范](TaskBarWidgets/AGENTS.md)。
