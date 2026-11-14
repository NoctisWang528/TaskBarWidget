# 验收状态

更新：2026-11-12。最近成功完整构建的路径由本机 `artifacts/latest-build.txt` 提供；构建产物、布局检查图、日志和用户配置不提交到仓库。

## 启动后静默预加载卡片（2026-11-12）

开机自启动与手动启动均在快捷卡片配置和任务栏入口就绪后准备卡片宿主，并等首份快照后沿用 250 ms 合并调度，创建启用卡片、布局并绘制隐藏首帧。按需模式通过现有管道只读请求音频、流量历史和天气状态；数据准备最多等待 10 秒，不订阅可见歌词、不强制刷新天气、不激活卡片。常驻模式继续隐藏更新；按需模式预加载完成后停止隐藏绘制，闲置 10 分钟释放窗口和绘图资源，迟到快照不会重新创建。提前点击复用显示路径；后台宿主启动失败保留手动点击重试。

原生卡片回归已通过：真实本机假后端验证等待初始快照、单次数据请求、延迟及错误回复、无可见歌词订阅和天气状态只读、释放后的迟到快照；屏外宿主验证首次窗口复用、两种资源模式、禁用卡片、提前点击、待执行预热退出及隐藏资源清理。保留原有绘制、DPI、动画、重开和 STA 退出回归。测试不启动正常产品或 Media 后端。

完整硬件 Release 构建 `20261112-182204`（版本 `261112-1822`）成功，最终包 WinUI 屏外模板检查通过；实际指针验证因用户桌面活动跳过，不计为桌面交互验收。

硬件／驱动初始化失败后的实际恢复、真实开机启动首开耗时、无闪现、焦点与混合 DPI 的桌面体验仍待实机验收；未启动或替换日用安装，未修改启动登记。本次用户另行授权提交、推送并发布版本 `261112-1822`，上传后直接发布，免除上传附件的下载复核；本地打包完整性检查照常执行。

## 更新器本地调试入口（2026-11-10）

新增默认关闭的当前用户本机管道与 `scripts/push-local-update.ps1`。以安装目录下 `Data/config/update-debug.enabled` 动态启用；支持本地完整附件目录导入、同版本重装及回退。沿用正常 ZIP／清单／逐文件校验、确认安装、草稿保护、运行库预检、备份及回滚，不在导入时执行包内程序。本地候选仅限当前会话，主动检查正式更新即退出本地模式，包括网络失败情况。

VSTest 在 testhost 配置父进程退出回调时仍遇到 Win32 错误 5，断言未执行；复用已有进程内入口，调用同一批托管更新 Fact／InlineData，共 59 个用例通过，不宣称 VSTest 全套通过。覆盖默认禁用、同版本及回退、完整性与路径拒绝、自动检查隔离、重启不恢复、取消结果关联、安装／下载互斥、运行时开关，以及畸形请求和响应大小。实际 PowerShell 推送／关闭脚本通过真实命名管道连接隔离协调器，仅用夹具替代进程发现，未启动正常产品。原生 `scripts/test-updater.ps1` 隔离事务、备份、退出批准、失败回滚及恢复检查通过。独立只读复查指出的正式通道恢复与响应上限问题已修正并加入回归。

本轮仅本地构建及交付，不启动、停止或替换日用安装，不更改启动登记。真实 Explorer 更新重启、管理员权限跨进程连接、播放／音频、混合 DPI 和个人数据实机验收仍待用户实际操作。构建与测试日志为 `artifacts/logs/local-update-*.log`，最终成功构建位置由 `artifacts/latest-build.txt` 提供。

用户已授权提交、推送及发布 261110-1631。沿用已通过完整构建和本地包检查的产物，提交后在独立干净检出目录重新打包，记录正式源码提交。按本次明确要求，附件上传完成后直接发布，不下载复验。日用安装与个人数据不变。

## 卡片动画速度与目标帧率（2026-11-09）

“快捷卡片 → 卡片与顺序 → 进出动画”新增 0.5×–3× 速度滑块，默认 1.5×；目标帧率可跟随当前屏幕（最高 240 FPS），或选择 30／60／90／120／144／165／240 FPS。保存、重读、旧配置默认值和两条原生配置通道均接通。速度同时缩放整组进入／退出的时间，保留屏幕底部完整位移、快速反向及减少动画行为。

组动画改用高精度一次性等待计时器，由卡片 STA 在同一消息循环中处理；没有新增动画线程。按目标帧间隔预约下一次更新，迟到时跳过过期时刻；动画结束取消计时器，线程退出关闭句柄。隐藏和空闲时不持续运行动画帧。

完整硬件 Release 构建 `artifacts/builds/20261109-220954`（261109-2209）通过。最终目录的设置模板探针通过，检查了速度／帧率草稿重绘保留和原有拖动布局，已查看新增控件的离屏截图。复用进程内入口调用实际设置持久化、旧配置及快照测试方法，共 18 个调用通过，不宣称 VSTest 全套通过。独立只读审查未发现原生计时器生命周期或消息循环的阻塞问题。

原生卡片回归通过，保留整组移动、反向、资源模式切换及线程退出检查。0.5× 速度下，30／120 FPS 档分别采样到 14／30 个不同位置；120 FPS 档从 0.5× 调到 2× 后，首次观测到运动至完成的耗时由 469 ms 缩短到 125 ms。日志位于 `artifacts/logs/card-motion-settings-*.log`。这些离屏窗口位置采样只验证调速与帧更新行为，不代表屏幕实际呈现帧率或 Mica 观感；真实鼠标探针没有收到 PointerPressed，未声称真实拖动通过。没有启动或替换日用安装，本轮仅本地交付。

用户随后明确授权提交、推送及发布 261109-2209。1122 个来源文件与已验证本地候选一致，仅追加本授权记录。正式发行复用上述完整构建，提交后重新打包记录正式源码提交；不重建、不重跑行为检查，保留一次正式包校验。按用户明确要求，附件上传完成后直接发布，不下载复验。

## 整组从屏幕底部进入与退出（2026-11-09）

进入和退出不再使用固定 48 DIP 位移，统一按卡片组顶部到当前显示器物理底边的距离，加上阴影余量计算。整组卡片从屏幕下方进入，退出时完整移出屏幕后才隐藏并恢复缓存位置；可选更新卡也参与同一组几何。进入采用 280 ms 减速曲线，退出采用 240 ms 加速曲线，反向开关从当前偏移接续并按剩余距离缩短时长。减少动画与高对比度继续立即完成。

完整硬件 Release 构建 `artifacts/builds/20261109-195200`（261109-1952）和原生卡片回归通过，覆盖进入／退出长距离位移、四个窗口同位移、快速反向与再次打开。离屏探针原先反复枚举整个桌面，会漏掉短动画的中间帧，现改为直接采样已知的四个窗口，并等待异步开关完成。最终退出采样最大位移 1081 px，组高 644 px；这只是离屏窗口几何证据，不作为实屏帧率或动画观感验收。没有启动日常安装。日志位于 `artifacts/logs/card-screen-motion-20261109.log`。

用户已授权提交、推送并发布 261109-1952。沿用上述完整构建，提交后重新打包记录正式源码提交；按本次明确要求，附件上传完成后直接发布，不下载复验。

## 常驻预绘与按需停止绘制（2026-11-09）

原生常驻卡片此前只在创建时预绘，隐藏期间的新快照仅标记内容变脏，打开又强制刷新环境并重绘。现在常驻模式在隐藏时用 250 ms 单次任务合并变化，提前创建、布局并绘制脏内容；显示复用已有表面。隐藏时停止歌词、封面和交互动画，数据未变不重复绘制。按需模式隐藏后不响应内容绘制消息，沿用 10 分钟资源回收；切回常驻可重新初始化已释放的绘图设备。离场动画结束后再预绘，重复布局不会自行形成预绘循环。

Media 保持真实可见性订阅，隐藏常驻卡片只接收权威媒体状态更新，不开启高频歌词呈现。已启用卡片复用现有音频、天气状态和流量历史读取通道：隐藏设备信息每 5 秒刷新，天气状态每 5 秒读取缓存，流量历史正常每 60 秒读取；重新显示时立即重新校验。天气和流量异步读回检查连接代次及当前模式，隐藏按需卡片不主动刷新这些卡片专用数据。设置说明与 README 同步移除旧歌词浏览器描述。

完整硬件 Release 构建 `artifacts/builds/20261109-193418`（261109-1934）通过。原生卡片回归通过：隐藏快照后的 WM_PAINT 绘制次数为 0、显式预绘提交一次变更、相同布局重新显示不增加内容绘制次数；另覆盖按需启动无卡片窗口、切常驻创建隐藏窗口、触发闲置回收后切常驻恢复。回收检查投递既有闲置定时器消息，不声称实等十分钟。复用已有进程内测试入口调用 CardSnapshotBuilderTests、NativeCardBridgeTests、MediaRenderSchedulerTests 的 15 个实际测试方法并通过，不宣称 VSTest 全套通过。独立只读审查未发现阻塞问题。

用户随后授权提交、推送并发布 261109-1934，沿用上述已验证的完整构建，提交后重新打包记录正式源码提交；按本次明确要求，上传后不下载附件复验。未启动或替换日常安装。真实首次点击耗时、DWM/GPU 资源、实际播放与混合 DPI 的流畅度仍需桌面验收；以上绘制计数不等同于帧率或零卡顿保证。日志保存在 `artifacts/logs/resident-warmup-*.log`。

## 卡片动效发行准备（261102-1615，2026-11-02）

用户明确授权提交、推送并发布本轮改动。代码提交 `8bfacad` 后重新进行完整硬件 Release 构建，版本 `261102-1615`，目录 `artifacts/builds/20261102-161504`。该包原生卡片回归、WinUI 屏幕外设置模板、共享 .NET／SQLite／字体、原生运行库解析、WinUI bootstrap 与缺失／错误版本回退检查通过。未启动或替换日常产品。

VSTest 在设置父进程退出回调时遇到 Win32 错误 5，断言前中止，未记为通过。复用现有进程内入口，在测试子进程专用的 artifacts 临时目录下调用同一批更新 Fact／InlineData，39 个用例通过；没有更改全局环境或进程权限，不将它描述为 VSTest 或原生更新事务通过。

已下载上一版正式 Release `261030-2353`，确认 ZIP 大小／SHA256 与其 `update.json` 一致，并确认本地旧构建的 Media 更新校验器 DLL 与下载包内文件逐字节哈希相同。发行包使用该旧校验器和当前校验器执行完整解压、逐文件哈希和持久化暂存检查；草稿附件回验、原生完整清单预检及正式发布状态以本地发行日志和验证产物记录为准。公开说明只包含用户可见变更和升级说明。实屏串画复测、高刷屏观感、实际点击／拖动和混合 DPI 的边界仍沿用下节记录。

## 流量绘制隔离、封面淡化与合成器开合（2026-11-02）

用户报告播放媒体时点击流量图会使流量卡出现媒体画面。日期选择分支只更新流量状态，不切换卡片种类。针对光晕绘制中途替换 Composition 借出的 D2D context 目标这一风险，改为在同一共享 D2D 设备上保留一个专用遮罩 context：光晕在它自己的位图上完成绘制后，再合成到卡片；不再改动借出 context 的 target，模糊初始化失败也不会遗留错误的 transform。独立审查未发现点击路径修改卡片种类的证据。这个改动隔离了可疑的共享绘制路径，但不把静态定位当作真实桌面问题已复现。

卡片 150 ms／菜单 160 ms 的透明度和 6 DIP 位移改为 Composition keyframe animation，移除开合所需的 16 ms 窗口 timer。内容先绘制，再开始入场；反向动画以 `this.StartingValue` 从合成器当前值接续，剩余时长按距离估算。完成事件只投递 HWND＋进程内唯一代次，不捕获窗口对象，旧动画完成不能收起后来重开的窗口。销毁时撤销事件和动画；DPI、减少动画与高对比度变化收敛到最终状态。命中坐标的 6 DIP 补偿采用同曲线的 CPU 时间估算，不声称它是合成器呈现进度读回，严重提交延迟时的边界命中仍属桌面验收。悬停／播放内容的按需 timer 保留。

封面先绘制完整新图／占位符，再叠加逐渐消失的旧图，修复新图完全遮挡淡出图的问题。隐藏、高对比度、减少动画时直接切换，重复同一图不开始过渡；清理不再需要的旧封面缓存。

验证通过：真实 Composition 借出目标在光晕绘制期间保持不变；既有多 DPI、明暗主题、atlas 偏移、中文换行、低等级光晕像素回归；封面 A→B、B→C 起点／中点／终点的 BGRA 和不透明度；卡片／菜单开合没有逐帧 WM_TIMER；快速开关反向、旧回调、销毁重建、固定 HWND 几何、减少动画和高对比度中途收敛。完整硬件 Release `261102-1603`（`artifacts/builds/20261102-160341`）与该包内的原生卡片回归通过，共享运行库包布局检查通过。没有启动普通产品。

可复用命令（工作目录为产品根目录，`<完整构建目录>` 指含 build-info.json 的隔离构建）：

```powershell
.\Native\tests\run-native-card-tests.ps1 -BuildDirectory <完整构建目录>
.\Native\tests\run-native-card-tests.ps1 -BuildDirectory <完整构建目录> -Mode Measure
.\Native\tests\run-native-card-tests.ps1 -BuildDirectory <完整构建目录> -Mode SurfaceIsolation
```

`Measure` 使用独立进程、合成数据和屏幕外窗口，按隐藏、静态显示、模拟媒体播放、20 次快速开关、最后隐藏的相同顺序，每阶段停留约 2 秒，记录 Working Set、Private Bytes、CPU 时间、内容绘制次数及 HWND 可见状态延迟。输出到忽略的 `artifacts/measurements/`；它不启动主程序、媒体服务、音频或真实播放器，不读取个人数据。基线源版本为 `4ed1c63`，前后探针及 JSON 保留在 `artifacts/diagnostics/20261102-card-motion-before` 和 `20261102-card-motion-after`。短时进程测量不覆盖 GPU／DWM 内存、真正的呈现帧率、十分钟按需回收和真实首次点击；不能据此宣称明显节省资源。

最终构建的测量输出为 `artifacts/measurements/cards-20261102-080628-959.json`（文件名使用 UTC）。静态显示阶段 Working Set 60.25 MiB、Private Bytes 70.26 MiB；模拟播放为 60.59／70.68 MiB；最后隐藏阶段内容绘制增量和 CPU 时间增量均为 0。旧实现两次空闲对照的静态显示为 Working Set 59.73／60.31 MiB、Private Bytes 70.52／69.59 MiB，内存处于相近范围。CPU 的 15.625 ms 计量粒度和短采样波动不足以证明降低占用；明确确认的变化是开合不再依赖窗口逐帧 timer。HWND 可见延迟不是首帧呈现延迟。

`SurfaceIsolation` 仅捕获本进程创建的屏幕外诊断窗口，交替更新媒体与流量并比较提交后的像素。本机 Windows Graphics Capture 返回“没有权限捕获该窗口”；已明确记录为未通过／不可用，没有以黑图或未运行当作成功。真实播放时点击热力图是否彻底消除串画、高刷屏观感、窗口动画期间边界点击、混合 DPI、Explorer 与用户实际音频环境仍待桌面复测。该开发阶段没有启动、停止或替换日常安装；用户随后于 2026-11-02 授权提交、推送和正式发布此批改动。

## 卡片迁入原生进程（分支 dsh/CardDrawingRebuild，2026-10-24）

四张快捷卡片与可选更新卡从 WinUI 迁入原生主进程（`Native/Cards/`），运行在独立 STA 线程上，使用系统 Composition、Direct2D/DirectWrite/WIC 绘制，材质由 Windows App SDK 的 `MicaController` 通过原生 Win32 目标承载。卡片属于 `TaskBarWidgets.exe`，打开卡片不再启动 WinUI；设置进程改为按需启动，并在最后一个窗口关闭后退出。Media 保留媒体、音频、天气、历史与设置业务，新增卡片专用管道 `TaskBarWidgets.Cards.<nativePid>`（`cardSnapshot`／`cardRequest`／`cardReply`），卡片请求与设置窗口共用同一套执行入口，设备切换、音量与静音的顺序语义不变。

**结构。** `CardHost` 独占卡片组几何与生命周期，共用一套 D3D11/D2D/DirectWrite/WIC 设备、一个 Compositor 和图形设备；`CardWindow` 只持有自己的窗口、合成根、绘图表面与交互表；`CardLayout` 直接移植 `CardGroup` 的 384 DIP 普通卡、220 DIP 更新卡、12 DIP 间距、列内底对齐与整组缩放规则；`CardPopup` 以同一套绘图辅助函数绘制设备与来源列表；`CardAccessibility` 从同一份交互表生成 UI Automation 元素（按钮 Invoke、滑块 RangeValue、选择器 ExpandCollapse、热力图 SelectionItem）。热力图光晕改为一次离屏透明遮罩＋集中模糊；封面 200 ms 交叉淡出；歌词换行上下交替、超长行滚动；扫描高亮仍是一条裁剪覆盖层。

**验证。** `scripts/build.ps1 -Configuration Release -Component All` 通过；发布产物设置模板探针通过；`Native/tests/run-native-tests.ps1` 全套通过（含管道传输与跨语言集成客户端）；Media 285 项测试通过。旧 WinUI 卡片路径（卡片窗口、歌词视图、外部点击监听、启动预热及相关探针）已删除。

**待桌面验收。** 本轮没有启动产品：明暗 Mica 观感、四张卡与更新卡的实际排布、设备／来源菜单、歌词扫描与换行过渡、滑块拖动、外部点击收起、设置关闭退出进程，以及混合 DPI、真实音频与播放器、高对比度与减少动画，都仍需在真实桌面确认。UI Automation 已实现但未经屏幕阅读器验证；封面淡出与光晕模糊的像素观感同样待实屏确认。

## 天气统一保存与来源显示（2026-10-24）

用户更正账户 Host 后确认上一版天气功能正常。本轮将来源、城市及连接草稿纳入顶部“应用更改”：先验证并保存连接，再保存城市或来源，失败保留输入；普通设置保存不再重建天气编辑器而丢失天气状态。待保存判断使用实际差异，撤销编辑后不再因历史编辑标记一直显示未保存。卡片将和风返回的归因页裸 URL 显示为“和风天气 · 数据来源”两个可访问链接，其他来源文字保留。

完整硬件 Release `261024-1906`（`artifacts/builds/20261024-190642`）编译通过，WinUI 发布资源及共享运行库布局检查通过。使用从产品源码提取的实际天气草稿方法，在当前 PowerShell 进程的托管模拟环境中检查连接验证失败保留输入、连接先于城市保存、不兼容城市阻止来源切换、隐藏连接输入不阻止切换中国天气网，以及城市已保存但刷新失败的错误处理，均通过。现有 WinUI 回归增加顶部按钮与来源命名链接场景并参与编译；遵守用户不启动软件的要求，没有运行产品、离屏探针或测试可执行程序，真实窗口交互仍待用户验收。没有修改用户 Host、Key、安装或网络配置。

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


## 2026-10-24 原生卡片审查与整合发布

审查起点为 `dsh/CardDrawingRebuild` 的 `428adda`，先合入当前主分支 `f841fdf`，保留资源占用优化、天气连接/AQI/预警迁移、Data 目录及设置保存修复。用户授权本轮直接修复、合并主分支、推送和发布；实际桌面测试由用户进行。详细问题与对原实现的评价见 [原生卡片代码审查](docs/2026-10-24-原生卡片代码审查.md)。

修复涵盖实际 D2D/Composition 设备接入与表面目标恢复、卡片 STA bootstrap/DispatcherQueue/退出、DPI 与输入坐标、拖动捕获与终值读回、请求封装和单写端、重连代际、图片增量与缓存、歌词动画和显示设置、天气空状态/归属、UIA apartment 与菜单状态、按需设置退出以及卡片闲置资源释放。另由发布检查发现并修复主分支既有 SQLite 侧文件回滚缺口，未把它归因于卡片分支。

完整硬件 Release 构建：`artifacts/builds/20261024-203345`，版本 `261024-2033`。Media 测试 315 项通过；原生既有回归与跨语言测试客户端通过；实际生产卡片代码的独立 NativeCardsProbe 通过，覆盖五种隐藏窗口、Composition/D2D 提交、遮罩目标恢复、100%/150%/200% 命中转换、卡片线程启动与正常退出。发布目录设置模板、共享 .NET/Windows App Runtime、原生托管启动探针通过。更新器隔离检查通过，包含新建 SQLite 侧文件清理、原有侧文件恢复和未知相似名称文件保留。

新增卡片探针不启动正常产品、Media 服务或真实音频，不读取个人配置，不显示或激活正常窗口。WinUI 离屏 PostMessage 不能证明真实 PointerPressed 拖动，本次没有把其记为通过。没有采集内存基线，也没有声称节省了某个实测内存数值。

待用户桌面验收：Mica 与动画最终观感、实际鼠标/键盘与菜单、真实音频与播放器、屏幕阅读器、Explorer 重建和混合显示器 DPI。媒体卡当前仍是单内容表面重绘，不能宣称已经完成全部静态/动态图层分离优化。


## 2026-10-24 任务栏点击未打开原生卡片

用户反馈升级 `261024-2033` 后任务栏可运行，但点击不出现卡片。源码确认 `NativeMediaHost::Impl::Panel()` 仍发送旧 `togglePanel`，Media 继续转发到已经删除卡片处理分支的 WinUI；新增 `ToggleCards()` 没有接到正常点击入口。上一轮探针只检查单卡离屏绘制和 Host 启停，漏掉入口接线及 Host 的完整开合路径。

修复：监测/媒体/托盘共用的 Panel 入口直接调用原生 ToggleCards；Media 的程序化/兼容入口改发 helper-to-native `toggleCards`，不再启动 WinUI。原生启动失败与开合请求记录到已有诊断日志。

补充入口源码守卫，以及真实 CardHost 在屏幕外打开四卡、关闭、重开的回归。隔离模式禁用外部服务连接、全局钩子及前台激活，检测实际 HWND 可见状态；不能据此宣称真实任务栏鼠标点击或桌面视觉已验收。

新增回归还复现重复初始化时的访问异常，栈落在 `MicaController::IsSupported`：bootstrap 卸载前残留 C++/WinRT activation factory 缓存。Runtime 退出前清理工厂缓存后，同进程重新初始化和开合回归通过。相关平台行为见微软 [C++/WinRT 缓存工厂指向已释放内存的排查](https://devblogs.microsoft.com/oldnewthing/20211105-00/?p=105878)。它是隔离复现额外发现的生命周期问题，不作为用户本次“点击无反应”的直接原因。

本地修复构建 `artifacts/builds/20261024-230817`，版本 `261024-2308`，完整硬件 Release 编译通过。卡片入口守卫、五种卡片绘图、100%/150%/200% 命中、真实 Host 打开/关闭/重开及退出回归通过。Media 单测本轮两次均在测试宿主启动阶段因读取父进程退出信息返回 Win32 错误 5 而中止，未进入断言，未将其记为通过。没有启动正常产品、读取个人配置或替换日用安装；本轮只生成本地测试包，未提交、推送或发布新 Release。

## 2026-10-24 流量卡光晕收敛（预览已确认）

- 原绘制将每格光晕向外扩张 3–5 DIP，超过 4 DIP 间距，再把模糊层盖在色块上；WithAlpha 覆盖原始透明度，进一步冲淡色阶。改为先绘制光晕再绘制不透明色块，光晕源不外扩、普通格内缩 1 DIP，模糊参数由 5 降为 2，透明度乘以原色 alpha。继续共用一次遮罩/模糊，不增加逐格效果对象；高对比度仍不绘制光晕。
- NativeCardsProbe 增加 `--traffic-preview <输出目录>`，直接调用产品 `Paint::Traffic`，用同一份示例数据输出 125% DPI 的深浅色 PNG。预览使用固定背景色，不代表桌面实时 Mica；不读取个人流量历史，也不启动正常产品。
- 本地前后预览位于 `artifacts/traffic-glow-preview/`。用户明确要求先确认视觉效果，再进行正式产品构建、提交、推送及 Release，本轮仅构建预览/检查程序。
- 验证：现有原生卡片探针通过（绘制提交、遮罩目标恢复、100/150/200% 命中、离屏卡片组打开/收起/重开及 STA 退出）；正式构建、提交和发布等待用户确认效果图。

## 2026-10-24 卡片入口与光晕修复发布 261024-2323

用户已确认由产品绘制代码生成的光晕修正预览，并明确授权将任务栏卡片入口修复一起构建、提交、推送及发布。此前按用户指定目录将本地安装更新到 261024-2308，完成全目录备份和数据哈希验证；之后用户提供了实际打开流量卡的截图。本次发布不另外覆盖日用安装或启动正常产品。

完整硬件 Release 构建 `artifacts/builds/20261024-232319` 通过。原生卡片入口守卫、绘图/遮罩恢复、100%/150%/200% 命中、离屏整组打开/收起/重开与 STA 退出通过；原生共享运行库与异常分支、Media 共享框架/无窗口设置/SQLite，以及发布产物的设置模板加载通过。模板探针因桌面活动未验证真实指针操作，不记为通过。

Media 测试重新尝试一次，仍在 VSTest testhost 设置父进程退出回调时返回 Win32 错误 5，断言尚未开始；本轮不宣称 Media 单测通过，也未更改系统权限或绕过保护。日志保留在本地 `artifacts/cards-release-media-tests.log`。原生绘制探针与桌面截图均不能替代完整音频、屏幕阅读器、混合 DPI 等实机验收。

发布流程：干净源码提交后生成 ZIP、app-files 清单、SHA256SUMS 和 update.json，以当前及旧版 Media 更新器验证 ZIP；上传草稿资产后下载比对哈希，并重新验证下载包，成功后再发布稳定版。开发日志、预览图、个人配置与历史不进入发行包。

## 2026-10-25 卡片刷新与二级菜单回归排查

检查已安装的 261024-2323 及当前日志。23:44:11、23:44:30、23:44:48 的任务栏点击均到达 Native card toggle；此前 23:43:52/54 等只有 down。现有日志没有记录按键取消/捕获/命中失败的具体分支，因此不能把所有偶发失败归结为同一个原因。未停止、启动或替换日用安装，未修改个人配置。

确认并修复：

- Media 每 400 ms 发布可见卡片快照，原生 WeatherFrom 对相同 revision 仍返回 changed，导致 RefreshSnapshot 对整组调用 Place/Show；Place/Show 即使位置与可见状态相同也重画并重新置顶。天气源与原生接收两端去重，稳定布局不重绘、不重启动画或调整 Z 序。不是流量历史查询死循环（历史读取周期仍为一分钟）。
- 媒体渲染器隐藏时仍对保留的浏览器帧做按钮命中，隐藏按钮可能吞掉监测界面的点击。HitTest 限定可见状态，入口同时确认按下/松开时的媒体显示与来源；移动判定改用屏幕坐标，避免窗口位置变化冒充拖动。增加捕获丢失、取消、松手拒绝与禁用媒体按钮分支日志。卡片部分创建失败后重开会补建缺失卡，零可见卡不会维持虚假的整组 visible 状态。
- 弹出菜单原锚点是整个卡片，且会被后续整组置顶盖住；改为实际选择器的屏幕边界。配置禁用所属卡片或布局改变锚点时关闭菜单；同一选择器重复点击切换展开/收起。
- 菜单保持不激活，由所属卡片转发方向键、Enter/Space、Escape、Tab 和滚轮。鼠标由按下立即提交改为捕获后同一项内松开提交，移出/取消不提交，关闭时释放捕获。补充 WM_PAINT 验证与绘制，修正滚动条 alpha 值。
- 音频快照可能先于同版本操作回复抵达；之前回复里的错误信息被 revision 去重丢弃。只合并同版本回复的反馈，不让旧版本修改状态。UIA 焦点回调同步卡片焦点与菜单选择，避免 Enter 操作旧项。

独立只读复核由 Claude Code 完成，初轮达到回合上限后要求停止工具调用并给出结论；所有结论逐项核对。未采纳“卡片 HWND 使用 NOACTIVATE”和“所有音频变更失败都不递增 revision”等与当前源码不符的判断。复核不代替实际回归验证。

完整硬件构建 `artifacts/builds/20261024-235839`（261024-2358）通过。原生渲染器生命周期回归通过，新增隐藏媒体命中检查。发布的 Media 程序集通过独立天气 revision 检查（同版本保持对象/版本、旧版忽略、新版递增），未使用无法启动的 VSTest testhost，未据此声称整套 Media 单测通过。详细日志保留于 `artifacts/card-interaction-*.log`。

仍需实机验证：偶发点击是否完全消失、真实设备/来源切换、混合 DPI 下菜单位置及屏幕阅读器操作。本次仅修复和本地验证，没有新的推送/Release 或日用安装替换。
最终 NativeCardsProbe 通过：相同天气快照无变化标记；100/150/200% 下连续 20 次不变布局不新增流量绘制；弹窗按下不提交、移出松开取消、同项松开仅提交一次、键盘跳过禁用项；同版本音频回复提示保留且旧回复不能覆盖；整组开合/重开与 STA 退出通过。

## 2026-10-25 Mica 激活保持与选择列表垂直对齐

用户反馈多张卡片 Mica 不能持续显示，以及设备下拉列表文字贴近行顶。最终保留原生 HWND + DesktopWindowTarget 路径：先 SetTarget 连接窗口，再 SetSystemBackdropConfiguration 应用统一的 IsInputActive=true 配置；WM_ACTIVATE/WM_NCACTIVATE 后排队恢复组配置，避免在窗口消息/材质回调内部重入。重新显示或改变环境时重新应用配置，不以一次失败永久禁止后续连接。切换 Mica Kind 后再设置自定义颜色和透明度；窗口销毁时显式 Close 控制器。系统高对比度/节能/透明度策略仍由系统控制器处理。

设备选择框、媒体来源选择框及其弹出列表使用完整行高的 DirectWrite 段落垂直居中；只设置本次 text layout，不修改缓存的共享字体格式，因此其他顶部对齐的标题和正文不变。

尝试过的自定义 ICompositionSupportsSystemBackdrop 材质层在本机合成队列验证中未通过，已完全移除；最终没有增加额外材质层、Compositor 或每帧材质轮询。平台 API 参考：[SetTarget](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.systembackdrops.micacontroller.settarget?view=windows-app-sdk-2.0) 与 [SetSystemBackdropConfiguration](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.systembackdrops.micacontroller.setsystembackdropconfiguration?view=windows-app-sdk-2.0)。

完整硬件 Release 构建 `artifacts/builds/20261025-002643`（261025-0026）通过。原生探针对五种卡片在 100%/150%/200% 下发送失焦消息、处理合成队列并断言 Mica 控制器实际 State 为 Active；同时验证前一轮的静态重绘、菜单提交取消和整组开合。测试窗口均处于屏幕外，不替代真实桌面材质观感验收。未覆盖日用安装、未启动正常产品、未提交/推送/发布。

## 2026-10-25 卡片多轮修复整合发布 261025-0040

用户明确授权修复媒体卡文字偏上、流量图偶发未加载，并将最近几轮累积改动提交、推送和发布，发布说明需覆盖全部未发布的卡片改进。

媒体空状态标题使用完整元数据行高居中，封面音符上下居中，歌词为空或只有一行时使用完整歌词视口垂直居中；有两行时分别居中，基础文字、阴影与扫描仍共用同一个布局。来源为空时显示“暂无播放来源”。通过实际产品绘制代码输出深浅色预览，确认 `artifacts/media-alignment-preview/media-empty-dark.png` 的布局。

流量问题：原可见卡片刷新先等待天气，再请求流量，并在请求前把下一次流量时间推迟一分钟；音频在途标志还会挡住整个刷新入口。改为三个独立在途标志，流量读取五秒超时，失败后两秒重试、成功后六十秒刷新；天气或音频等待不能阻止流量重试。流量先验证再递增 revision 和替换缓存，无效/部分响应保留原数据；跨连接的旧回复不更新当前卡片缓存。

完整硬件 Release 构建 `artifacts/builds/20261025-004052`（261025-0040）通过。原生卡片探针、Mica 失焦状态检查、菜单提交/取消和静态重绘回归通过；最终构建的共享 .NET/SQLite/无窗口设置检查与设置模板探针通过。发布 Media 程序集的定向反射检查确认无效流量响应不覆盖有效数据或版本，恢复后正常递增，以及相同天气版本不重复更新。全量 Media 单测仍因 VSTest testhost 的父进程访问错误 5 在断言前中止，未记为通过。离屏模板没有真实鼠标拖动验证，不记为通过。

本轮汇总发布此前的隐藏媒体命中、卡片重复刷新、菜单定位与键盘/滚轮/鼠标提交、音频操作反馈、UIA 焦点、Mica 激活和文字对齐修复。用户日用安装不在本次发布步骤中替换。发布包在干净提交后生成，通过当前与上一个稳定版本更新器验证，再上传草稿、下载校验后发布。

## 2026-10-25 运行内存采样与卡片文字/元素抖动

采样对象是用户正在使用的 261025-0040，进程 Native 33076 / Media 35912，未运行设置进程。未改动个人配置、未强制 GC/裁剪工作集、未启动或停止产品。第一组约 30 秒采样时卡片收起；第二组由用户手动展开四卡，共 24 个样本，约 58 秒。原始数据保存于本地 `artifacts/card-memory-sample-20261025.json` 与 `artifacts/card-memory-open-20261025.json`，不作为发行包内容。

四卡展开时：两进程平均工作集约 109.7 MiB（Native 70.5、Media 39.2），平均私有工作集约 45.2 MiB（Native 30.1、Media 15.1），合计私有提交 298.2–316.0 MiB；Native 私有提交 208.3–209.3 MiB，Media 89.5–106.9 MiB。24 个逻辑处理器上合计整机 CPU 平均约 0.48%。工作集包含共享页，私有提交不等于驻留物理内存；这些数字不能相加，也不包括单独统计的 GPU 显存。期间提交内存与句柄均有回落，没有持续单向增长，但短样本不能排除长期泄漏，也不能与旧“500 MB”在口径未知时计算节省比例。

确认的渲染问题与修复：

- 重复的媒体位置快照每次重置本地插值时钟，导致已经向前绘出的进度回退。只有实际位置、播放状态、来源、曲目或时长变化才重置；真实向后 seek 保留。
- 同一行歌词收到重复扫描值时清零速度并重设基准，且 150 ms 外推窗口会在较慢推送间隔内停顿。扫描采用独立时钟，重复值保持速率，有限外推增加到 500 ms；同一行向前播放不退回已显示扫描位置，实际向后值/换行/暂停重新对齐。
- 每条快照及 Tick 都调用 SetTimer，重设相同定时器会推迟下次 tick。现在仅在所需周期改变时重设。
- 每次重绘重建 CompositionSurfaceBrush 是已确认资源抖动，而不是已证明的画面根因；改为按实际 surface 对象复用。绘图表面的实际 D2D context 明确设置灰度文字抗锯齿。
- 播放歌词的双行基线不再随次行暂空而上下切换；空状态文字继续居中。弹出菜单先绘制再显示，避免短暂露出旧列表。

只读独立复核（Claude Code）确认插值与定时器问题。未采纳“popup transform 每帧累积缩放”的判断，因为 DrawScope 已明确重置 atlas translation；也未引入每帧完整 Snapshot 拷贝，因为 Host 持有权威状态，CardWindow 的模型本就是显示副本，并已有独立原始插值锚点。

完整硬件构建 `artifacts/builds/20261025-130637`（261025-1306）通过。原生定向回归覆盖：重复快照不回退媒体位置/歌词扫描，高频快照仍触发动画 tick，真实向后 seek 正常生效，连续绘制保持同一 brush 身份，以及既有 DPI/Mica/菜单/卡片开合检查。日志为 `artifacts/card-jitter-tests*.log`。这验证了代码中的确定性缺陷，不等同于证明用户看到的所有文字抖动已消失，仍待实机复测。本轮未提交、推送或发布，也未覆盖用户安装。

用户随后明确授权本轮提交、推送及 Release。发布复用已验证的完整构建 261025-1306；提交前核对源文件与本地候选包记录的哈希一致，仅追加本段发布记录。正式包从干净提交重新生成至 artifacts/releases/261025-1306-publish，保留原本地复测包；当前和上一个稳定版本更新器验证、草稿上传及下载校验通过后发布。内存采样原始文件不进入发行包。

## 2026-10-26 首次流量显示、卡片动效、预警与托盘图标

本轮按用户要求只使用内置子代理，修复并本地验证，不启动或替换日用安装，不提交、推送或发布。

- 流量首次连接的缓存原本为空，历史查询到首次显示之后才开始。现在每次管道连接时预取一次历史，等待期间绘制十二周占位网格，未加载的总量与峰值显示破折号。原有独立查询、超时重试和版本保护保留；新增真实 NativeSettingsClient / NativeCardBridge / CardSnapshotBuilder 链路检查，覆盖首次加载与取消后重新请求，并确认同一连接收到完整 84 天数据。原生离屏检查确认空状态到首批数据无需关闭重开即可重绘。
- 恢复二级菜单的淡入淡出及位移动画，卡片开合使用缓出过渡并可反向取消，鼠标命中跟随实际内容偏移。按钮补上悬停、按下反馈，热力格子的同色光晕随悬停渐变；过渡完成即停表，隐藏、强制关闭、减少动画及高对比度均有收尾路径。菜单主题随卡片同步。
- 天气设置增加预警总开关与和风来源开关，并纳入原生保存白名单；和风请求遵守总开关，关闭后失败缓存不能恢复预警。中国天气网异常预警条目不会覆盖有效缓存；卡片对只有标题的预警显示标题正文。多条预警的轮播定时器与单条预警的静态状态均有离屏回归。
- 默认托盘槽位加载产品图标，自动主题适配的旧浅色默认值迁回产品图标；保留其他手动图标选择。选择图标后先执行适配，再读取实际句柄。
- 删除无调用的旧 WinUI 面板切换类及对应测试、UI 中未使用的流量模型链接、旧卡内菜单骨架、重复动画定时器、未使用的圆角几何缓存和天气状态标志。旧天气 SVG 作为有许可声明的设计参考保留，并显式排除默认 Content 收集，避免继续进入 PRI 与产物。

验证记录位于 artifacts/logs/*20261026.log。原生卡片探针通过首次加载、100/150/200% 绘制、悬停过渡停表、动画中菜单行命中、重开取消隐藏、立即收起、预警轮播、媒体插值、Mica 与 STA 生命周期检查。天气提供方离线场景回归通过。发布目录的设置模板与天气开关草稿探针通过，真实指针检查因桌面正在使用而未执行。深浅色流量、加载态与仅标题预警的产品绘制预览位于 artifacts/tests/cards-20261026-previews/，已检查；固定背景图不代表真实 Mica 观感。

VSTest 仍在 testhost 访问父进程时返回错误 5，断言前中止；改用 artifacts/tests/card-contract-runner 中的进程内入口调用同一批实际测试方法及 InlineData，29 个用例通过，不宣称整套 VSTest 通过。NuGet 附带 MakePri 在本机返回 E_ACCESSDENIED，使用已安装 Windows SDK 的 MakePri 重新生成当前资源后，scripts/build.ps1 完整硬件构建和资源哈希检查通过；未改动系统权限或全局环境。

实际 Explorer 托盘显示、真实城市当前预警、鼠标手感、混合 DPI 和日用播放器/音频仍待桌面验收。

补充验证：保存预警配置遇到正在刷新的任务时，安排一次后续重载；多个保存请求合并到同一个排队任务，队列容量保持上限。旧城市或旧来源的预警不会随失败缓存重新出现，普通天气信息仍保留并标为过期；新城市／新来源成功刷新时保留新的预警与署名。上述场景由事件同步的离线天气测试验证通过。

最终完整硬件构建为 261026-1440（artifacts/builds/20261026-144022）。最终目录的原生卡片、设置模板与共享运行库探针全部通过；新产品图标的九种尺寸资源与 assets/app-icon.ico 图像数据逐字节一致，包括托盘使用的 16px。产物中没有旧天气 SVG。代码和验收记录保留在工作区，日用安装与个人配置未改动。

用户随后授权提交、推送及发布本轮 Release。提交前核对当前 1116 个来源文件与本地候选包的源码哈希完全一致，候选目录的 157 个对应运行文件也与已验证包一致；仅追加此发布记录。正式发行复用完整构建 261026-1440，从干净提交生成至 artifacts/releases/261026-1440-publish；以当前版本和上一稳定版本的更新器验证，草稿上传后下载复核全部附件，再发布为稳定 Release。本次不覆盖日用安装或更改启动注册。

## 2026-10-28 卡片启动崩溃与反馈入口修复

用户提供的 261026-1440 崩溃转储在本地只读取异常、模块及 delay-load 信息，没有上传转储。异常是 0xC06D007E；DelayLoadInfo 指向 Microsoft.Internal.FrameworkUdk.dll 的 Containment_GetChangeEnabled，Win32 错误 126。该 DLL 实际存在于 Windows App Runtime 2.6.4 的框架包中。

隔离控制台复现了原调用顺序：主线程 CheckWinUiRuntime 返回成功并立即 Shutdown，WindowsAppRuntime.dll 仍在进程内；随后新 STA 再次 bootstrap 抛出相同的 delay-load 异常。根因是启动预检查撤销了包搜索依赖，之后仍在同进程使用运行库。进一步的嵌套初始化／关闭检查还发现后续初始化返回 0x80040014，因此最终方案是原生主进程只保留一份 bootstrap，卡片 STA 仅管理 DispatcherQueue，不再各自初始化／关闭 bootstrap。

WinUiRuntime 在启动检查时取得引用，保留到 ExitInstance。退出先显式停止并释放卡片宿主，再清理 WinRT 工厂缓存、释放 bootstrap，最后 RoUninitialize；NativeMediaHost 的其他 shared_ptr 持有者不会延后卡片 STA 的停止。加载保护只将 0xC06D007E／0xC06D007F 转成 HRESULT，缺失模块／入口使用正常依赖错误路径，其他 SEH 不吞掉。没有复制系统 DLL、更改全局搜索路径或修复／安装系统运行库。生命周期约束参考 [微软 bootstrap 文档](https://learn.microsoft.com/windows/apps/windows-app-sdk/use-windows-app-sdk-run-time)。

崩溃对话框沿用的中英文上游文案已替换为本项目 Issues 地址、本地转储位置和手动反馈说明，移除原作者邮箱及不存在的“选项／插件管理”指引；不自动发送任何内容。外置旧语言文案缺少本项目入口时改用内嵌英文。最终 EXE 的中英文 TEXT 资源与源语言文件及包内语言文件逐字节一致，崩溃条目中没有原作者邮箱或旧菜单指引。许可和上游归属保持不变。

完整硬件构建 261028-1417（artifacts/builds/20261028-141733）通过。最终包的依赖检查通过真实已安装运行库、缺失引导文件、不支持的版本、模拟缺失 DLL／入口、其他异常不被误吞，以及跨 STA 保持包依赖；原生卡片探针通过两次独立卡片宿主 STA 的创建／销毁、现有绘制和 Mica 检查。第一次卡片探针曾在既有的 200ms 弹窗隐藏断言超时，后续两次通过；未将它作为 bootstrap 故障证据。共享 .NET／无界面设置检查及最终设置模板探针通过，离屏窗口未产生真实鼠标拖动事件，未记为实际拖动通过。

原生更新器测试已编译，但 Windows 在启动隔离的模拟 Media 宿主时返回“操作已被用户取消”，事务测试未完成、未记为通过。未尝试绕过该启动取消；测试的 finally 已发送退出信号，未停止任何日用进程。日志：artifacts/logs/runtime-lifecycle-final-20261028.log、cards-runtime-final-20261028.log、shared-runtime-20261028.log、ui-runtime-20261028.log；隔离复现源码位于 artifacts/tests/runtime-crash-20261028/。

本轮仅本地修复和打包；未启动／替换日用安装，未改个人配置，未提交、推送或发布。真实 Explorer／硬件／播放器和安装后的完整启动仍待用户实机验收。

补充：现有托管更新器测试在默认临时目录创建夹具时被拒绝；仅为测试子进程指定产品 artifacts 下的 TEMP/TMP 后，39 个原有 Fact/InlineData 用例以进程内方式全部通过。未改全局环境，未启动产品或模拟宿主 EXE；这不替代前述未完成的原生事务测试。日志为 artifacts/logs/update-contracts-workspace-20261028.log。

用户随后授权提交、推送及发布此修复。提交前核对 1116 个来源文件与本地候选包记录完全一致，157 个对应运行文件也与已验证包一致；仅追加此发布记录。正式发行复用完整构建 261028-1417，从干净提交生成至 artifacts/releases/261028-1417-publish，经当前及上一稳定版本的包校验器检查后创建草稿，下载核对全部附件再发布稳定版。保留上述验收限制；不覆盖日用安装，不上传崩溃转储或个人数据。

## 2026-10-30 卡片悬停与自动重绘稳定性

本轮按用户确认方案实施，只交付本地修复包；不提交、推送、发布，也不启动或覆盖日用安装。天气正文、字体、四秒预警轮播、IPC、提供方、持久化配置和运行库初始化保持不变。

- CardWindow 按区域保存 140ms 缓出过渡，切换、离开和立即返回都从当前强度接续；允许多个控件同时淡出，归零删除状态，稳定后停表。隐藏、禁用、高对比度和减少动画及时收尾。
- 将悬停、按下和键盘焦点反馈放回各控件背景层，先画反馈、再画文字图标。圆形按钮保持圆形，选择器保持原圆角，滑块反馈限制在轨道和滑块；删除原来覆盖整个命中矩形的通用路径。
- 热力格光晕源固定内缩 1 DIP，低等级格子的光晕和未知格描边也连续从零接续，不再跳入基础亮度。选中和焦点反馈保留。
- DrawScope 统一 96 DPI、DIP 单位、缩放矩阵和 Composition 的物理像素偏移；卡片和二级菜单共用入口。像素回归进一步定位到光晕模糊受图集位置影响，改为在表面本地坐标生成 mask，再按原偏移合成，保持原有模糊 DIP 半径。
- 禁用中的滑块取消捕获及待提交值，不提交过期手势；禁用 seek 的首帧保留快照中的真实位置。

完整硬件 Release 构建为 261030-2353（artifacts/builds/20261030-235320），Native、Media、WinUI 和共享运行库布局检查通过。定向原生探针通过：快速跨三个区域和返回、淡出清理/停表、禁用/高对比度/减少动画、取消拖动、菜单 DPI 命中与 HWND 不移动、开合取消、首次流量加载、预警轮播、媒体进度/歌词扫描及 STA 重建。

新增像素回归覆盖五种卡片、100/125/150/200% DPI × 100/93% 工作区缩放 × 深浅主题，以及三个绘图表面原点。固定长中文预警的逐行长度、行高、基线在交错绘制其他卡片及淘汰字体格式缓存后保持一致；相同原点重复绘制、自动快照和悬停外静态区域逐字节一致。跨原点比较额外验证命中几何不变；GPU 矢量边缘仅允许 1/255 的最终通道量化差，实际最大值为 1，单次最多 189 个像素。修正 mask 前，100% 浅色流量卡平移到 (17,29) 有 12,405 个像素变化、最大通道差 3；该样本修正后逐字节一致。未通过关闭刷新、延迟绘制或整卡缓存隐藏差异。

日志：artifacts/logs/build-card-stability-final-20261030.log、cards-stability-complete-20261030.log；深浅色长预警、媒体悬停和热力图预览位于 artifacts/tests/cards-stability-261030-2353/，已人工检查。最终 ZIP 目录的检查及校验和另记于本地交付目录的 ACCEPTANCE.md。

这些结果来自真实产品 painter、隔离屏外 HWND 和固定背景预览；未启动正常产品，不能替代用户桌面的 Mica 观感、真实鼠标手感、混合 DPI 显示器、Explorer、播放器和硬件验收。完整构建仍有未改动模块的既有资源宏重复及链接警告。

用户于 2026-10-31 随后授权本修复的提交、推送和 Release 发布。发布准备核对 1117 个来源文件与已验收本地包全部一致，159 个包内文件及其对应完整构建输入也全部一致；只追加此授权与来源核对记录。正式发行沿用完整构建 261030-2353 的真实构建时间，从干净提交重新生成来源清单和 ZIP，经新旧版本校验器及草稿附件下载校验后发布。上述实机验收限制继续保留，不启动或覆盖日用安装。

## 2026-11-03 媒体分层重绘、天气增强与整组窗口动画

本轮按用户要求实现三项改动，保留原生 Win32 / Composition / Direct2D 架构。DeskBox 仅用于布局参考，没有复制其 GPL 源码。所有实现、构建和诊断均在产品目录内；不启动正常产品、不覆盖日用安装或个人配置。本轮没有提交、推送或发布。

- 媒体静态层保留标题、来源和控制按钮；封面、歌词、进度使用独立动态层。相同快照、暂停闲置及无关卡片变化不再触发媒体重绘，点击区域只在布局或能力变化时重建。播放进度按250ms更新，歌词扫描和封面渐变按33ms更新，过渡结束或隐藏即停表；DirectWrite 布局缓存最多64项。隔离样本中歌词扫描触发3次动态绘制，静态绘制和点击区域重建均为0；这不是整机 CPU 或 FPS 的前后对照测量。
- 天气改为固定388 DIP高度的概览、指标、日出日落和预报区，今天／预报切换及翻页不改变窗口尺寸。和风请求最多24小时、7天预报，7日失败时回退同来源3日；小时失败保留同城有效缓存并显示错误，切换城市清理旧小时数据。中国天气网保留实际日期标签与最多7天有效预报，不补造缺失的小时、降水、紫外线或日出日落数据。完整观测时间、署名、预警和过期提示保留，无效温度显示破折号。小时每页6项，每日每页5项。
- 四个完整卡片窗口共用一个时间源和垂直偏移，通过一次 DeferWindowPos 事务一起从下方48 DIP进入，包含背景、边框和内容。进入240ms、退出160ms，反向切换从当前位置继续；每帧只移动窗口，不触发内容绘制或重建布局。菜单打开前结束组动画，DPI重排合并到位置事务之后；减少动画及高对比度直接定位。
- 天气小图标在独立 D2D context 中按缩放和亚像素相位生成局部缓存，再原位合成，最多64项，设备重建时清空。此举消除新增小图标在不同 Composition 图集原点下的边缘差异，不更改原矢量图形，也不借用正在绘制其他卡片的 context target。

完整硬件 Release 构建为261103-1243（artifacts/builds/20261103-124349），Native、Media、WinUI、共享运行库布局均通过。最终目录的原生卡片回归、系统共享 .NET / 无界面服务探针与设置模板探针通过。媒体测试覆盖暂停、重复快照、扫描插值、封面渐变、拖动能力取消、命中区域保留、字体/DPI及隐藏；组动画测试覆盖四个HWND共同位移、原布局不重叠、停止位置、退出反向、进入中关闭、重开及STA关闭。天气提供方离线测试与本机回环HTTP测试通过，包括小时失败、同来源回退和城市切换；没有读取个人天气密钥或向天气服务发起真实凭据请求。

像素回归保持原门槛：相同原点重复绘制逐字节一致；跨图集原点仅容许1/255通道量化差，覆盖100/125/150/200% DPI、100/93%缩放、深浅色及缓存淘汰。产品 painter 导出的28张天气场景预览通过布局检查，并检查了预报、预警、过期、空状态及极端温度图像。日志位于 artifacts/logs/build-card-upgrade-final-20261103.log、cards-final-20261103.log；预览位于 artifacts/tests/cards-20261103-previews/。

离屏诊断和固定背景预览不替代实际 Mica 观感、Explorer、真实播放器/音频、鼠标手感或混合DPI显示器验收。设置模板探针未收到真实PointerPressed，未宣称拖动通过；Windows此前拒绝的窗口捕获没有绕过或记为成功。完整构建仍有原有未改动模块的资源宏和链接警告。

补充：根据用户截图修正每日预报图标对齐。原128×128设计画布中雨云位于右下，太阳接近中央，相同绘制坐标未使可见图形对齐。现在在共享矢量绘制入口按各符号的可见中心（含光线、降水和阴影）统一居中，保留原有大小和图形；今日概览与小时预报同步适用，不新增逐帧测量或缓存。完整硬件构建261103-1254（artifacts/builds/20261103-125458）通过；原有卡片回归及严格像素检查通过，28张天气预览通过布局检查，并检查了每日深浅色及小时预报对齐。日志为 artifacts/logs/build-icon-alignment-20261103.log、cards-icon-alignment-20261103.log；修正预览位于 artifacts/tests/weather-icon-alignment-20261103/。本次仍为本地交付，保留上述桌面验收限制。

用户随后明确授权提交、推送和发布本轮全部改动，并要求不过多校验。提交前核对1122个来源文件与已验证本地包的来源记录一致；仅追加本授权记录。正式发行沿用完整构建261103-1254，不重建、不重跑行为测试，从干净提交生成正式来源清单及ZIP，仅执行必要的包校验和草稿附件下载核对，再发布稳定Release。保留前述实机验收限制，不启动或替换日用安装。

## 2026-11-03 设置卡片拖动对齐

根据用户截图修复“卡片与顺序”的拖动错位。拖动浮层原在标题栏所在的 Grid 第0行，命中坐标却相对第1行的导航内容，产生48 DIP偏移；现在两者共用内容区坐标。行让位使用 WinUI 的 Translation 动画，保留布局位置，重复指针移动或滚动时不重启相同目标，从当前动画值接续反向移动；取消时停止并还原位移。

命中依据稳定的布局槽位，不随让位动画摆动。同列落点扣除移出卡片所占的位置；跨列高亮对应最终空位，覆盖末尾占位时隐藏“拖到这里”以免透字。离开有效区域清除预览，松手时按实际位置重新判断目标。共用的播放器来源排序同步使用稳定布局命中和取消收尾。

完整硬件 Release 构建261103-1318（artifacts/builds/20261103-131833）通过。最终发布目录的设置模板和排序探针通过，覆盖浮层原点、同列／跨列／空列、重复动画目标、减少动画、动画中取消、区域外释放及单次提交。检查了同列末尾、跨列末尾和跨列首位的离屏预览，高亮与目标槽位对齐。日志为 artifacts/logs/build-settings-drag-final-20261103.log、settings-drag-final-20261103.log；图片位于该构建的 ui-layout-check/settings-card-drag-*.png。

本轮为本地修复交付，未提交、推送或发布。真实鼠标探针因用户桌面活动而跳过；上述程序化状态和离屏渲染不等于实际鼠标手感、Mica或混合DPI验收。未启动或替换日用安装，未修改个人配置。

用户随后明确授权本次提交、推送及 Release，并要求不过多校验。1122个来源文件及157个对应构建输入与已验证本地候选一致，仅追加本授权记录。正式发行复用261103-1318完整构建，不重建、不重跑行为测试；从干净提交生成正式来源清单和ZIP，执行必要的包校验及草稿附件下载核对后发布。上述实机验收限制继续保留。

## 2026-11-10 外设电量本地候选

实现共享外设电量采集、独立任务栏模块、详细/缩略原生汇总卡及设备选择设置。卡片与任务栏分别控制，动态身份和原有布局配置保留。新增固定版本 HIDAPI 隔离采集子进程及默认关闭的 HeadsetControl 4.1.0 扩展，随包保留许可和对应源码。DB179 的 262 条接收器记录中，261 条有直接协议实现，一条通过可选扩展；全部标记待实机验证，接口和报告不匹配时不发送查询。完整限制见 docs/外设电量.md 和兼容清单。

完整 Release 构建 261110-1816 位于 artifacts/candidates/peripheral-integration。142 项托管 xUnit 测试通过；系统 VSTest 父进程监视因访问拒绝无法启动，使用真实 xUnit AssemblyRunner 运行测试，日志为 artifacts/peripheral-inproc-tests.log。采集子进程契约测试通过，包含取消、超时、重启、初始化记录、管道写入失败及恢复。最终原生协议自测通过，含 QHW 型号板号、Neon collection、GLW 直接索引、Lofree 回复头、DAILING 充电和耳机显式断连回归。

原生卡片 Regression、发布目录 WinUI 模板（含15项外设设置交互）、内置模块启动和共享运行库检查通过。电量卡片离屏覆盖 100%/125%/150%/175%/200% DPI、明暗、高对比度和减少动画。SurfaceIsolation 因 Windows Graphics Capture 返回“没有权限捕获窗口”未通过，不能据此宣称实际 Composition surface 隔离验收完成；离屏 WinUI 的 WM_MOUSE 消息未提升为真实 PointerPressed，真实鼠标拖动也不计为通过。真实设备充放电、休眠唤醒、厂商软件共存、Mica 和混合 DPI 待验收。

用户要求直接打包，已停止资源测量，不报告 CPU/内存达标。未启动正常产品、替换日用安装、修改启动注册或个人配置，未提交、推送或发布。已有更新服务相关改动保留。本地候选按工作区来源生成清洁 ZIP，诊断日志、截图及个人数据不入包。

首次打包校验发现更新器没有接纳新增根目录文件和 HeadsetControl 扩展。仅为托管校验器及原生更新器增加明确文件白名单，扩展目录只允许固定版本程序、源码 ZIP、LICENSE 和 README，其他路径仍拒绝。最终完整构建版本为 261110-1826，最终候选目录为 artifacts/candidates/peripheral-261110-1826；不使用未通过校验的 261110-1816 ZIP。


## 2026-11-10 外设实机修复与旧更新器兼容

完整构建261110-2149位于 artifacts/builds/20261110-214947。修正 Windows watcher 属性名，使系统电量与标准BLE BAS链路恢复；按明确电量能力过滤普通HID/电话端点。实机读取：G502 X 27%、Razer BlackShark V2 HS 2.4 76%（后续75%）、ROG AZOTH 100%充电、小米耳夹耳机100%逐步降至88%、FlyDigi BS2标准BAS 94%。G502 X空闲时仍可能不响应首次发现；已确认的单元在运行期间保留旧读数，未把未响应解释成0%。不宣称完整睡眠/唤醒周期已验收。

用户将夜魔切到有线且保留接收器后，1A83/1A85两路唯一USB父序列号相同，实测只保留一个当前键盘、优先有线、稳定ID不变。修复HID字符串尾部垃圾导致身份漂移的问题；只迁移精确可证明的旧别名，历史未知身份不按名称猜测合并。ROG/Razer/Xiaomi使用固定Simple Icons；罗技按用户指定G HUB程序的图标重绘为单色G标志，独立保留品牌权利说明。预览已核对，缓存随既有Graphics::Release释放。

Windows/服务53项定向测试、更新器81项托管测试、原生协议与进程契约检查通过。最终原生卡片Regression、WinUI发布模板、内置模块启动、系统共享运行库检查通过。离屏真实指针验证因userDesktopActivity跳过；不将离屏结果当作Mica、Explorer和混合DPI实机验收。没有进行资源测量。

261110-1631已撤回为草稿，附件保留，正式latest退回261109-2209。实际261109-2209和261110-1631 Media DLL均生成协议2作业；各自真实DLL校验兼容外层ZIP，旧helper经隔离FakeHost夹具完成旧helper→桥接→最终ROOT布局。桥接保留原备份与日志，覆盖内层损坏、健康失败、交接中断、恢复与回滚。portable ZIP继续使用真实新目录布局，update.json指向旧白名单兼容包；本地推包仍复用同一流程。协议1作业不属于此桥接的兼容范围。

本地推包脚本用PROCESS_QUERY_LIMITED_INFORMATION核实管理员进程的安装路径；管道权限不足时明确提示使用管理员PowerShell。不会通过放宽ACL或自动提权绕过系统限制。源程序、配置、日志与硬件身份证据仅留本地；发行包不包含个人数据。

2026-11-10 22:35 本地卡片修复交付：261110-2230（完整 All/Release）。设备读数区域不再发出操作；开放式耳机头梁；HID++ 0005 实测名称 G502 X LIGHTSPEED、电量25%；同一稳定身份重新发现时保留正确名称。分页使用 Composition 180ms 淡入／位移，模式切换复用 CardHost 有限计时器同步 HWND 高度和邻卡位置，目标内容只绘制一次。读数状态改为连接方式与成功读取时间，刷新失败保留原电量和时间，内部错误仅留诊断快照。

25项电量服务回归通过，原生卡片Regression通过（包含只读点击、时间更新、尺寸过渡不逐帧重绘），离屏模板加载通过。定向预览核对耳机图标及长时间文本；HID++协议和子进程契约检查通过。WinUI离屏窗口没有把合成WM_MOUSE消息提升为PointerPressed，不宣称真实指针拖动通过；没有CUA和资源测量。实际Mica及混合DPI桌面视觉仍未人工验收。

两个ZIP均通过清单解包和持久化校验。经正常退出，完整备份并核对361个个人文件后，只复制portable清单程序文件，已恢复原生与Media进程；安装版本和关键文件哈希复核通过。证据位于artifacts/diagnostics/peripheral-followup，备份为artifacts/personal-backups/TaskBarWidgets-before-261110-2230-20261110-223535。没有提交、推送或发布新Release。

用户验收261110-2230本地版本后，明确授权本轮提交、推送和正式Release。发布复用22:30开始的完整All/Release构建，程序源码与已验收候选来源哈希一致；仅更新README、发布说明及两种原生卡片渲染示例，并将内部任务交接记录移至忽略的artifacts目录。图片使用示例数据，不作为桌面Mica截图。正式包从干净提交生成，按发布流程先上传草稿、下载核验附件并检查两版旧更新器兼容后才转正式；不重复硬件测试或资源测量。

## 2026-11-11 AirPods 免驱估算与耳机关机隐藏（261111-0804）

用户选择免驱估算并接受同型号邻近匹配限制。新增 Apple 邻近广播 provider，复用 Windows 发现目录，严格解析27字节0719帧和已知型号；已配对经典蓝牙身份保存配置，8秒被动扫描、至少两包、多个同型号候选拒绝更新。关闭需求/开关取消扫描；提前终止窗口不采信样本，晚停止不并发启动第二个 watcher。左右耳、盒独立显示，估算值带≈，未知分量保留—。设置提供默认开启的独立开关，并增加附有来源许可的 Apple 标志。

BlackShark V2 HS 1532:0565通过实机0020成功回复明确区分关机/开机，校验状态、长度、填充和XOR。接收器仍在时的明确断连维持普通查询间隔，开机自动恢复；接收器拔除和不再有需求时停止。保留上次电量，不把超时或真零电量判成关机。服务测试覆盖重复断连、恢复、移除与蓝牙断连不主动查询。

All/Release完整构建、127项定向托管检查（最终发布程序集）、原生协议自测、原生卡片Regression及已发布WinUI模板检查通过。首次托管复核仅因测试运行器残留旧版TaskbarLyrics.Core引用失败，换入本次完整依赖后通过。真实指针检查因userDesktopActivity未执行；无CUA、资源测量、混合DPI或Mica桌面视觉验收。独立Astra复核提出并修复扫描提前停止后不应接受样本的问题。

实机：Pro 2 USB-C型号2024已连接，左右耳≈80%、盒未知，附近Max未被匹配；双耳对称读数不能证明非对称及充电的实机正确性。Razer关机、接收器保留时连续三次disconnected，开机后online/74%，在线和断连帧载荷及校验均已核实。

两种本地ZIP均通过解包/清单校验。安装于08:11:04完成，362个个人文件先备份核对，仅替换portable清单程序文件并正常重启，关键程序哈希复核通过。安装后的服务缓存产生新时间戳：Razer 08:11:15在线74%，AirPods 08:11:22左右耳≈80%；读取现有配置确认卡片开启、断连隐藏为true，两设备均被选择且符合卡片显示条件。外部PowerShell管道诊断被既有peer路径校验拒绝，未绕过校验；后续采用服务持久化读数及配置核对，不宣称取得实际UI截图。

本地证据在artifacts/airpods-20261111，交付在artifacts/candidates/airpods-261111-0804；备份为artifacts/personal-backups/TaskBarWidgets-before-261111-0804-20261111-081057。未提交、推送或发布这一增量；上一正式Release261110-2230保持原状。
## 2026-11-11 外设名称滚动与多电池布局（261111-0820）

详细模式将AirPods等多电池设备的左右耳、充电盒读数放到右侧分行显示，去掉该行电量条，名称和连接方式/读取时间保留在左侧。详细及缩略模式溢出的名称使用独立Composition裁剪层横向往返滚动，品牌标志固定；相同文本布局复用缓存，隐藏停止，减少动画和高对比度回退静态省略，辅助功能保留完整名称。

完整All/Release构建、原生卡片Regression及WinUI发布模板检查通过。针对滚动核对实际Composition动画控制器、运行时无WM_TIMER/内容重绘、隐藏/恢复缓存及辅助功能切换。48张合成预览覆盖两种布局、主题、高对比度、100%–200%缩放与动画设置，抽查右侧读数不重叠。静态预览不作为真实桌面滚动/Mica验收。WinUI离屏合成消息未提升为PointerPressed，不宣称真实指针操作通过。没有CUA或资源测量。

首次像素检查沿用旧电量位置，按新布局修正允许变化的右侧读数区域；随后一次回归由外部GamePP GPP64.dll注入崩溃中断（Windows应用事件1000确认），未修改外部软件，重新运行完整Regression通过。证据在artifacts/peripheral-layout-20261111。

08:27本地交付：两种ZIP均通过完整解包/清单校验；正常退出并备份核对362个个人文件，仅替换171个portable清单程序文件，个人数据未变化。安装版本261111-0820及全部程序哈希复核通过，原生与Media进程已恢复。备份为artifacts/personal-backups/TaskBarWidgets-before-261111-0820-20261111-082719，结果在artifacts/peripheral-layout-20261111/replacement-result.json。本次仅本地更新。

用户随后授权提交、推送并发布261111-0820，明确要求上传后直接发布、不再下载附件校验。本轮复用已验收的08:20完整构建；与本地候选记录的1215个来源文件核对，程序源码一致，仅README和验收说明发生文档变更。正式包使用干净提交重新生成，保留本地清单/解包校验及双ZIP更新兼容布局；发布说明加入AirPods布局渲染示例。


## 2026-11-12 卡片液态玻璃与材质选择（261112-2058）

新增 LiquidGlass / Mica / Solid 稳定材质 ID；玻璃只保留透明到模糊一个参数，深浅主题共用。旧配置继续使用 Mica，保存、草稿预览、页面退出和后端重连贯通。参考 AndroidLiquidGlass 的圆弧折射与圆角方向反光，Windows 原生分带近似和平台限制记录于 [卡片材质](docs/2026-11-12-卡片材质.md)，Apache 2.0 许可和来源记录随产品保留。

完整 All/Release 构建 261112-2058、产品边界和出厂默认检查通过。MakePri 最初因本机临时目录访问拒绝失败；最终构建使用项目缓存的原工具，只为当前构建进程把 TEMP/TMP 指向 artifacts 下的可写临时目录，未改工具链或全局设置。发布版 WinUI 模板探针通过，报告明确包含材质显隐、玻璃滑块预览、高对比度草稿、Mica 禁用、重连恢复主题/草稿、离页撤销和草稿保留。

23 项相关托管测试方法与断言通过同进程隔离检查器执行。标准 VSTest 被 testhost 读取父进程的权限错误中止，不记为标准测试运行通过。原生真实效果图创建/复用、六类卡片主题/DPI/材质/高对比切换和空闲计时器检查曾完整通过；一次完整 Regression 亦通过。后续完整回归受外部 GamePP 注入崩溃或超时中断，Windows Application 1000 记录 GPP64.dll，不能把末次运行计为通过；未更改外部软件、叠加层或驱动设置。

独立 LiquidGlass 像素探针对本进程的屏外合成窗口捕获被系统以“没有权限捕获给定窗口”拒绝，未生成可验收的玻璃像素预览。真实桌面透射、细线背景边缘连续性、动态背景、多卡及 200% DPI 的 GPU 开销均待验收；当前原生适配含中央背景和十二个透镜带的模糊节点，不宣称零 GPU 开销或与 Apple 私有实现逐像素一致。WinUI 合成鼠标消息未被提升为真实 PointerPressed，真实指针拖动亦不记为通过。

本地证据：artifacts/builds/liquid-glass-dev/build-info.json、ui-template-check.json、native-card-LiquidGlass-error.log，以及 artifacts/logs/liquid-glass-*.log。候选只作开发构建；未启动正常产品、替换日常安装、提交、推送或发布。

## 2026-11-12 玻璃方框、控件反馈与初始化开销修复（261112-2134）

用户实屏反馈显示 2058 的反光贴图停留在卡片中央，旧效果图创建成功未证明正确呈现。九宫格源改为 Fill；背景改为底层可调模糊、外到内的十二个透镜视觉层、染色、反光和正文。透镜固定零半径采样后做仿射与 AlphaMask，避免 Windows 拒绝直接变换 backdrop，也避免效果画刷嵌套输入。每卡效果工厂由十七个降为两个，取消十二份重复的可调模糊；首次创建等待真实尺寸和 DPI。按钮／选择框及可拖动滑块新增反光、悬停、按压／拖动反馈，使用既有悬停计时器，命中与动作不变。

All/Release 261112-2134 构建通过，目录 artifacts/builds/liquid-glass-corrected。完整原生回归重跑通过，覆盖六类卡片、材质／主题／DPI／高对比切换、资源复用、空闲计时器、绘制稳定性、命中、预热、反复打开和 STA 释放。新增真实 D2D 控件像素比较通过，覆盖深浅色、100/150% DPI、悬停／按压／焦点／禁用与减少动态效果；PNG 位于 artifacts/previews/liquid-glass-fix，仅证明前景控件，不是玻璃背景实屏图。独立审查未发现新的具体生命周期或图层顺序问题。

隔离四卡 LiquidGlass 测量：cardPreparationMs=32.703，首次 HWND 可见状态到位=11.021 ms，20 次连续切换通过；静止显示阶段无正文重绘。该准备时间不含共享 Graphics／Compositor 初始化，可见状态时间不等于动画完成或 GPU 首帧。证据为 artifacts/measurements/liquid-glass-fix.json。

期间一次窗口回归在 ntdll.dll 中发生 0xc0000005，伴随 GamePP IPC 错误日志，不能据此确定其根因；随后同一源码完整回归通过。此前 2118 是不兼容直接 backdrop 变换的中间产物，已在其目录标记 NOT-FOR-DELIVERY，不作为交付包。当前原生探针证据保留于 artifacts/builds/liquid-glass-fix/native-card-Regression-recheck.log、glass-controls.log 和 glass-measure.log；该目录的产品 EXE 是旧中间产物，探针为后续修复源码构建。

自有合成窗口的 WGC 捕获仍被系统以“没有权限捕获给定窗口”拒绝。已补充整卡四边反光、中央无源尺寸伪影、边缘折射及清晰／模糊端点的像素断言，但本机此次未执行到这些断言，不能记为通过。真实桌面折射、边缘连续性、混合 DPI、动态背景和 GPU 帧时仍待实屏验收。此次未启动正常产品、替换日用安装或更改外部叠加层设置。

最终 2134 构建的 WinUI 模板探针通过。通过 package-release.ps1 -AllowDirtyLocal 生成 artifacts/candidates/local-update-261112-2134；便携 ZIP 的 173 个文件、兼容更新 ZIP 的 178 个文件均完成解压、持久化清单与 SHA-256 校验，update.json 指向兼容更新 ZIP。此为本地候选包，尚未导入正在运行的安装；未提交、推送或发布远程 Release。


## 2026-11-12 玻璃故障后的修正候选

可见玻璃探针运行后出现重复 DWM 故障（dwmcore.dll，0x8000000B），导致用户会话关闭并重启。独立探针进程并不隔离桌面合成器，具体根因未证实。本轮移除所有 backdrop 仿射变换及零半径采样绕过，停用 --glass-preview。卡片背景保留一次标准模糊、主题染色和边缘高光；控件改用普通颜色图层，缩放只作用于自身轨道。真实桌面折射尚未实现。

修正天气分段胶囊、城市／来源的多余框、鼠标焦点框、胶囊滑块／静音开关与抓取范围；修正 Unknown Artist 等占位艺术家回退为具体应用名称，媒体来源不再用“浏览器”覆盖实际应用名。帧调度移除超时后额外等待一帧的延迟。以上为实现状态，不能代替实测帧率或视觉验收。

按用户要求减少测试，本次不再启动可见探针或图形回归，仅进行静态检查、完整构建与打包校验；构建和本地更新结果随后记录。


最终完整 All/Release 构建为 261112-2253（artifacts/builds/glass-controls-final）。构建及静态 diff 检查通过；package-release.ps1 -AllowDirtyLocal 对便携包与兼容更新包完成解包、清单和哈希校验，候选为 artifacts/candidates/local-update-261112-2253。最终构建修复天气装饰胶囊与翻页按钮复用 406/407 的编号冲突；2245 中间候选标记为不可交付。

最终包通过现有 push-local-update.ps1、同一用户的管理员 PowerShell 导入成功。用户在应用内确认更新后，台北时间 2026-11-12 22:59:40 更新作业完成，journal 为 phase=complete、bridge=true。23:00 只读复核：安装 build-info 与 Native／Media／UI 版本均为 261112-2253，三个关键程序文件 SHA-256 与最终构建一致，Native 和 Media 已重启运行。正常更新器完成备份和健康流程；未复制覆盖安装、未修改启动注册、未提交／推送／发布。真实玻璃外观和帧率仍由用户验收，不把安装健康检查视为图形稳定性或桌面折射验收。

## 2026-11-13 实时折射候选（261113-1121）

用户接受必要时使玻璃卡片／菜单不出现在截图和录屏中，优先实时背景。新增 Desktop Duplication 捕获、应用私有 Direct2D 圆角位移与模糊，再交给 Composition 普通 surface；没有恢复 backdrop 仿射或零半径绕过。捕获只在正常可见卡片期间运行，首次初始化延后至展开动画结束；隐藏、材质退出和失败时释放图像并恢复窗口捕获标记。跨 GPU 使用非阻塞双暂存；失败最多三次尝试。设置页说明截图限制。

滑块头改为 20×14 DIP，小按钮圆形、大按钮胶囊形；电量翻页按钮包括禁用状态也使用玻璃外观，天气选中胶囊加入 280 ms 放大回弹。现有媒体艺术家／来源名、材质 ID、单一模糊滑块和主题规则保留。

静态审查与 git diff --check 通过。独立审查发现 GetWindowDisplayAffinity 的 layered-window 前置条件不适合作为本进程 Composition HWND 的 setter 门槛；按官方文档取消该门槛，仍以 SetWindowDisplayAffinity 的成功返回决定是否开始捕获。完整 All/Release 261113-1121 构建通过，位于 artifacts/builds/glass-live-refraction-final。兼容包和 portable 包的完整解包、清单／哈希校验通过（178／173 文件），候选位于 artifacts/candidates/local-update-261113-1121。261113-1116 为修正该前置条件之前的中间构建，不用于交付。

遵照用户减少测试的要求，未运行图形探针、图形回归、自动桌面捕获或正常产品预览。真实截图排除、背景扭曲、呈现帧率、跨 GPU、旋转屏、混合 DPI 和设备丢失行为仍由用户验收；构建成功不证明这些硬件行为。仅授权通过现有本地更新入口交付，不包含提交、推送或远程发布。

台北时间 11:28，通过同等管理员权限的既有 push-local-update.ps1 导入成功，返回 verified and ready；日志和状态位于 artifacts/checks/local-update-261113-1121。已请求用户在设置／关于页确认“更新并重启”。暂存不等于安装，安装结果待现有更新流程完成后记录。

用户确认更新并重启后，只读核对安装 build-info 与 Native／Media／UI 版本均为 261113-1121，三个关键程序文件 SHA-256 与最终构建一致。更新作业 dcfcb76917934be48192811e3bdb9fe5 为 phase=complete、bridge=true，Native PID 77268 与 Media PID 34856 已于台北时间 12:13 重启运行。证据为 artifacts/checks/local-update-261113-1121/installation-result.json。安装已确认完成；实际折射外观、截图排除和帧率仍由用户验收，本次核对没有启动图形探针或自动截图。

## 2026-11-15 折射回退与黑底修正（261115-0032）

只读核对上一版 261115-0015 已安装：主程序 SHA-256 与交付构建一致，更新作业 complete、bridge=true，Native／Media 已重启。用户报告仍无折射或色散并存在黑底。

源码确认 Direct2D Saturation 属性只接受 0–1，原管线却传入 1.5；改用 ColorMatrix 实现 1.5 倍饱和度。移除固定 42% 深色／48% 浅色覆盖，改为深色 2.5%／浅色 6% 白色提亮；控件输入边界外清为透明。保留私有 HLSL 七次光谱采样、捕获与回退结构。新阶段日志限约 64 KiB，仅含 PID、时间、阶段、HRESULT 与帧计数，不含图像或个人内容。

Heisenberg（gpt-6.1-sol）完成捕获／诊断的静态检查并实现 CardHost 日志，主智能体复核实际代码并连接具体折射失败阶段。FXC ps_4_0 /O3 纯 CPU 编译通过；git diff --check 与完整 All/Release 构建通过（00:32:35–00:34:07，artifacts/builds/refraction-fallback-fixed-20261115）。没有运行图形探针、自动抓屏、图形回归或产品预览。旧版本没有持久阶段日志，因此不能排除用户机器上更早的捕获失败；实际运行与视觉效果待更新后核对。打包、本地暂存及安装结果另行记录。

261115-0032 已通过本地接口暂存并由用户确认安装。只读核对 Native/Media/UI 三个关键文件哈希均匹配，journal 2ed54e2e862644e5843a53803cffce2a 为 complete、bridge=true。用户打开卡片后，新增日志明确显示 capture start=0x887A0004（DXGI_ERROR_UNSUPPORTED）、成功帧数为0；确认存在饱和度之前的捕获阻断。

## 2026-11-15 捕获兼容后备（261115-0047）

保留 Desktop Duplication 优先路径，遇到 DXGI_ERROR_UNSUPPORTED 时使用 Windows Graphics Capture 指定显示器的两帧池；沿用窗口捕获排除、非阻塞轮询和私有 GPU 图像，不恢复 Composition backdrop 变换。共享设备启用 D3D 多线程保护，复制到自有纹理后关闭借用帧，停止时关闭会话和帧池；首次三秒无帧走有界重试。未声明的 DDA 旋转使用输出旋转或无旋转。日志保留后备前的具体失败阶段。

Heisenberg 静态复核未发现所有权或生命周期阻断，提出保留原始失败阶段的诊断建议，已落实。完整 All/Release 261115-0047 构建通过（00:47:07–00:48:23，artifacts/builds/refraction-capture-final-20261115）；0043 是补充诊断前的中间构建，不交付。git diff --check 通过。未运行图形探针、自动抓屏或图形回归。WGC 系统捕获边框可能保留，HDR/混合DPI/跨显卡和真实外观尚待用户验收；不调用边框权限弹窗。最终包与安装结果另记。

261115-0047 便携包173文件、兼容包178文件的解包／清单／哈希验证通过，候选为 artifacts/candidates/local-update-261115-0047。已通过既有本地更新接口暂存并返回 verified and ready，等待用户应用内确认后核对安装及实时诊断。
随后只读核对确认 0047 已完成安装：job 31574857188941758610af9a506c68f7 为 complete、bridge=true，Native PID26192；Native/Media/UI 三个关键文件与构建哈希一致。用户选择稍后更新，但安装已于 00:50:26 完成，以实际日志为准。用户打开卡片产生的日志显示原 DDA 阻断位于 duplication-format，WGC 成功启动并向渲染送入背景，接下来在 refraction/create-lens 报 0x80070490。它尚未进入 live-refraction，不能把捕获成功当作视觉效果成功。

自定义透镜延迟注册只处理 D2DERR_EFFECT_IS_NOT_REGISTERED，漏掉 CreateEffect 同样支持返回的 ERROR_NOT_FOUND。已在锁前／锁内两处识别这两个返回值，并细分编译、注册、实例化诊断阶段。只进行静态检查、构建与打包；不运行图形探针。新候选先暂存，尊重用户稍后安装的选择。

修正版 261115-0054 完整 All/Release 构建通过（artifacts/builds/refraction-registration-fixed-20261115）；便携173文件与兼容178文件的解包／清单／哈希验证通过。候选 artifacts/candidates/local-update-261115-0054 已经既有接口 verified and ready（管理员 helper exit0），未自动安装。用户选择稍后安装，不再次催促确认。当前核实安装为0047；0054的透镜注册和实际视觉效果仍待用户更新后手动展开验证。

## 2026-11-15 实时折射安装确认与提交（261115-0054）

用户确认已更新并展开卡片，反馈“还不错”，授权本轮累计改动提交并推送，明确暂不发布 Release。只读核对安装版本为 261115-0054，Native/Media/UI 三个关键文件 SHA-256 均与最终构建一致；作业 234f21e8c6444b3abe998901f89e01c6 于台北时间 00:57:20 完成，phase=complete、bridge=true，Native PID195704。证据存于 artifacts/checks/local-update-261115-0054/installation-result.json。

用户打开卡片留下的日志已进入 live-refraction，HRESULT=0，累计成功处理2047帧；多次关闭／展开后都恢复实时路径。说明 WGC 后备、透镜注册及绘制链路已在当前安装运行。该计数不是屏幕呈现帧率，也不代表跨显卡、HDR、旋转屏或混合DPI均已验收。保留截图／录屏排除和可能出现系统捕获边框的限制。此次只读核验未运行图形探针、自动截图或额外图形回归。

提交前核对产品源文件在最终构建开始后没有变化；新增改动仅为验收和来源说明。复用0054完整构建、包校验与用户试用证据，不重复构建；提交排除构建产物、日志、个人配置和本地参考仓库。

## 2026-11-15 开合动画实时玻璃（261115-0119）

此前液态玻璃在呼出动画中故意保持标准模糊（`entrance-delay`），收起开始即停止捕获，动画结束后才切到折射，形成明显跳变。现在呼出开始即启动捕获，开合全程实时折射与色散：每帧先移动整组 HWND，再按新位置渲染各卡玻璃，同一批 Composition 提交送出；60 Hz 采样计时器在动画中只协调不重复渲染。收起保持捕获和截图排除，完全移出并隐藏后才停止；收起中重开不重启捕获。控件小透镜在动画中约 30 Hz 更新，末帧补齐。背景采样在显示器边缘按边缘像素延伸，卡片经过屏幕底边时不再向透明衰减。跨 GPU 输出的独立 D3D 设备首次创建延到动画结束，之后缓存复用；同 GPU 路径不受影响。

离屏计时（无窗口、无 Composition、无截图排除；`artifacts/scratch/glass-bench`）：五张 576×450 卡片的完整滤镜链每帧 0.5–0.76 ms；同 GPU Desktop Duplication 的 DuplicateOutput 0.5–12 ms（首次较慢）、首帧 4–24 ms；新建硬件 D3D 设备 140–300 ms。本会话 WGC 显示器捕获被系统拒绝（0x80070005），未测得 WGC 启动耗时；用户机器实际走 WGC 后备，其首帧前的少数动画帧仍为标准模糊，但此时卡片位于屏幕下方。

完整 All/Release 261115-0119 构建通过（artifacts/builds/glass-motion-20261115）；git diff --check 通过。遵照用户减少图形测试的要求，未运行原生卡片图形回归、图形探针、自动抓屏或产品预览。HWND 位移与 Composition 提交不保证同一 DWM 帧，可能偶发一帧背景错位；动画期间的实际帧率、跨 GPU、WGC 首帧延迟、混合 DPI 与观感待用户真机验收。因截图排除，录屏无法记录卡片，需肉眼或相机观察。未打包、未推送本地更新、未提交。

261115-0119 经 package-release.ps1 -AllowDirtyLocal 打包，便携173文件、兼容178文件的完整解包／清单／哈希验证通过；update.json 选择兼容 ZIP（SHA-256 2e2edd7a…f707717），与 SHA256SUMS.txt 一致。候选为 artifacts/candidates/local-update-261115-0119。经既有 push-local-update.ps1 在同一用户的管理员 PowerShell 推送到运行中的 Documents 安装（调试标记已存在，未带 -EnableDebug），返回 verified and ready，未自动安装。安装需用户在应用内确认；完成后再核对 journal 与安装版本。

## 2026-11-15 收起残影修正（261115-0134）

用户安装 261115-0119 后确认开合玻璃正常，但收起后残留五个透明空框，卡片内二级菜单变为不透明、看不出折射。只读枚举本进程窗口：五个卡片 HWND 均 IsWindowVisible=false、capture affinity 已恢复为 0，说明框体是 DWM 残留而非仍显示的窗口。0119 把停止捕获移到收起动画结束，并且先 SW_HIDE 再恢复 affinity 与模糊材质；旧版本在窗口仍显示时恢复。现改回“先恢复、后隐藏”：动画末帧卡片已在屏幕外，先 StopBackdrop 再隐藏；无动画收起路径同样处理。残影由隐藏后修改 affinity 引起是推断，未在系统层面证实。

二级菜单：静态核对未找到本轮改动影响菜单渲染的路径（菜单仍由 60 Hz 计时器渲染，日志无失败阶段）。新增诊断阶段 live-refraction+menu，菜单实际获得实时折射时记录一次，用于下一轮判断菜单是否进入实时路径或退回不透明绘制。

完整 All/Release 261115-0134 构建通过（artifacts/builds/glass-ghost-fix-20261115），git diff --check 通过；便携173／兼容178文件解包、清单、哈希验证通过，update.json 选择兼容 ZIP（SHA-256 0ed4f0b4…c574935）。候选 artifacts/candidates/local-update-261115-0134 已经既有接口在管理员 PowerShell 推送，返回 verified and ready，待用户应用内确认安装。未运行图形回归或探针，未提交。

## 2026-11-15 菜单折射卡片内容（261115-0141）

用户安装 261115-0134 后确认收起残影消失；音频卡二级菜单仍不透明、看不出效果，开关玻璃另有小问题（细节待用户描述）。日志在每次打开菜单时记录 live-refraction+menu，说明菜单已在实时折射，并非回退。根因：卡片窗口被排除于捕获，菜单采样到的是卡片背后的桌面，卡片自身的文字和控件在菜单下完全消失，观感如不透明面板。现在菜单逻辑打开期间，与菜单重叠的卡片暂时取消截图排除并冻结自身玻璃，菜单因此折射真实卡片内容；菜单开始关闭即恢复排除，卡片等两张更新的捕获帧后再刷新，避免采到自身。代价是菜单打开期间该卡片会出现在截图／录屏中。

完整 All/Release 261115-0141 构建通过（artifacts/builds/glass-menu-20261115），git diff --check 通过；便携173／兼容178文件解包、清单、哈希验证通过，update.json 选择兼容 ZIP（SHA-256 3995b399…c477f77bd）。候选 artifacts/candidates/local-update-261115-0141 已在管理员 PowerShell 经既有接口推送，返回 verified and ready，待用户确认安装。未运行图形回归或探针，未提交。菜单观感、取消排除切换时是否闪烁、关闭菜单后卡片自身是否出现重影待真机验收。

## 2026-11-15 液态玻璃正式发布准备（261115-0152）

用户确认已完成开合动画、二级菜单和色散修正，授权提交、推送与发布；提交沿用用户 Git 身份，不添加 AI 协作者署名。本次对比上一正式 Release 261112-2010：主要新增液态玻璃材质、控件和动画，另包含 Unknown Artist 占位内容及浏览器具体名称的显示修复，不重复上一版已有的卡片翻页／图标及更新器长路径修复。

新鲜完整 All/Release 构建通过（11/15/2026 01:52:10 至 11/15/2026 01:53:10，artifacts/builds/liquid-glass-release-20261115）；构建前后修改的原生源码 SHA-256 一致，git diff --check 通过。只读核对用户现有0141安装日志已出现 live-refraction+menu；本轮没有图形探针、桌面自动截图或重新运行图形回归，没有替换日常安装。

发行包在提交后由 package-release.ps1 从干净源码状态生成，并执行本地解包、清单及哈希检查；结果保存在 artifacts/checks/release-liquid-glass-20261115。先上传 draft，核对附件元数据后发布。用户明确免除上传后重新下载验包，不执行该步骤。公开说明只包含功能变化、截图／录屏行为和下载方式。
