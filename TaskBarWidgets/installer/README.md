# Windows 安装包

安装包由已验证的干净便携程序包生成，不从日用安装目录收集文件。它只安装清单内的程序、依赖和许可证，不包含 API 密钥、个人配置、日志或历史记录。

从仓库根目录运行（先准备 NSIS 3.13 编译器）：

```powershell
TaskBarWidgets/scripts/package-installer.ps1 `
  -PortableDirectory '<已验证的 portable 目录>' `
  -OutputDirectory '<TaskBarWidgets/artifacts 下的新目录>' `
  -CompilerPath '<NSIS/makensis.exe>'
```

脚本先验证 `app-files.json` 内的每个文件、大小与 SHA-256，拒绝额外文件和个人数据路径，然后生成精确的安装及卸载文件列表。安装器源码位于 `TaskBarWidgets.nsi`，使用未修改的 NSIS zlib 安装器引擎；交付时附上官方 `COPYING` 许可说明。官方编译器和文档见 [NSIS](https://nsis.sourceforge.io/Download)。

安装包适用于 Windows 11 x64，需要管理员权限。默认安装至 Program Files 下的 TaskBarWidgets，添加开始菜单快捷方式；桌面快捷方式可选且默认不选。只允许初装至空文件夹，已有安装请使用程序内更新。安装后不自动启动程序、不设置开机启动、不自动安装运行库。

程序需要 .NET 8 Desktop Runtime x64 和 Windows App Runtime 2.5.1 x64 或兼容的更新 2.x。缺少运行库时，现有程序启动检查会提供下载入口。

卸载前需正常退出该安装中的程序与设置窗口。卸载前核对安装根、程序子目录与文件均未被改成链接；卸载只删除打包清单中的程序文件及安装器创建的快捷方式，不递归删除安装目录。运行后产生的 `Data` 和个人资料会保留。

打包不会安装或启动产品。交付前应使用支持 NSIS 的解压工具解开安装器，逐个比对 `installer-manifest.json` 的有效载荷哈希；安装向导和实际卸载仍需要独立桌面验收。
