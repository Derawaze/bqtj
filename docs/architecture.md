# 实现地图

只描述当前实现；待做事项见 [backlog](backlog.md)，历史故障原因见 [回归要点](regressions.md)。

## 运行链

管理面板 → 每账号独立WPF容器 → 独立x86原生宿主 → Shell.Explorer.2 / 本机Flash ActiveX。
面板与容器由同一主程序启动；发布时压缩进自包含单文件，原生宿主始终独立。

| 修改目标 | 先读 |
|---|---|
| 面板/账号编辑/组合根 | src/BqtjLauncher.Desktop/ |
| 账号与会话规则 | src/BqtjLauncher.Domain/、src/BqtjLauncher.Application/LauncherModule.cs |
| 明文凭据与事务 | src/BqtjLauncher.Application/IAccountEditor.cs、src/BqtjLauncher.Infrastructure/SqliteAccountEditor.cs |
| 容器工具栏、加载层 | src/BqtjLauncher.Runtime.Flash/FlashHostWindow.cs |
| 命令超时、取消与回执编号 | src/BqtjLauncher.Runtime.Flash/NativeHostCommandChannel.cs |
| 启动/回执/生命周期 | FlashGameRuntime.cs、NativeFlashHostController.cs（同上目录） |
| 日志目录与诊断ZIP | Infrastructure/DiagnosticBundleExporter.cs、Desktop/MainWindow.xaml.cs、Desktop/App.xaml.cs |
| 启动器更新检查 | Application/LauncherUpdate.cs、Infrastructure/GitHubLauncherUpdateSource.cs、Desktop/MainWindowViewModel.cs |
| 游戏入口版本解析 | Application/GamePageHtml.cs、GamePageResolver.cs、Runtime.Flash/HttpGamePageSource.cs |
| 共享窗口/静音与账号变速偏好 | GameRuntimePreferenceStore.cs、SpeedPreferenceStore.cs（同上目录） |
| IE/Flash宿主、Cookie、窗口居中 | native/FlashHost/native_flash_host.c |
| 来源检查、填充、单次登录 | native/FlashHost/login_autofill.h |
| 连续虚拟时钟 | native/FlashHost/virtual_clock.* |
| 图标/发布/清理 | src/BqtjLauncher.Desktop/Assets/、tools/ |

## 关键边界

- 应用层通过 IGameRuntime / IGameSession 使用容器，不依赖具体WPF/COM。领域层不引用UI和存储。
- 每个宿主首次创建IE前启用进程内Cookie模式，失败停止；刷新重建浏览器但不重置会话模式。
- 管道传凭据用限长UTF-16十六进制，防止分隔符注入；这是编码而非加密。密码不进命令行、URL、剪贴板或日志。
- 填充限定可信HTTPS文档、同表单字段和提交目标；保留用户输入，完成一次点击后不自动重试。
- 显示按原生客户区计算整数百分比和居中黑边；WPF负责窗口档位与加载层，就绪等待后固定1秒黑底。
- 游戏包装页按版本发布，入口地址是运行时数据：每次启动读取4399官方游戏页得到当前版本路径，换到可直连主机后交给原生宿主；官方页不可用时依次回退上次成功解析的缓存和随包兜底地址。
- 原生输入线程使用ReadFile；就绪后托管端由唯一后台任务读取stdout。需回执的操作串行且带请求编号，超时只结束请求等待，迟到回执按编号丢弃；凭据与resize仍为单向命令，共用写入锁。WPF只传实际客户区，原生端在resize、导航完成与show时校准。查询当前光学倍率，仅在不匹配时写入，减少闪帧。
- 原生x86宿主开启大地址支持，增加x64 Windows上的地址空间余量；刷新显式Stop导航、关闭旧OLE实例并销毁窗口，保留进程内Cookie。该改动不代表已修复游戏内部内存泄漏。
- Flash计时导入只在当前宿主内替换，连续结算后切换倍率；音频通过宿主PID控制。
- 共享偏好用命名互斥、临时文件原子替换及分字段更新，防止多开互相覆盖。

## 本地数据

%LOCALAPPDATA%/BqtjLauncher：

- launcher.db：账号记录及按Guid关联的明文凭据；创建/编辑用事务，删除账号级联删除凭据。
- runtime-preferences.json：共享窗口档位、静音。
- game-page.json：上次成功解析的游戏入口及时间，仅在读取官方页失败时用于回退。
- speed/：各账号上一档变速。
- logs/：运行日志，不得包含凭据。

现有AppContainer兼容性类和 native/Diagnostics 为探针，不是正式隔离方案。当前只保证已验证的运行时Cookie行为，不外推Flash全部本地存储。



## 技术选择

- WPF/win-x86承载本机Flash ActiveX；每账号独立原生宿主隔离故障。同进程多ActiveX曾使面板崩溃，不能退回该方案。
- 变速只替换当前宿主中Flash OCX计时导入，切换前按旧倍率连续结算；旧变速DLL进入.NET曾引发CoreCLR崩溃，不再加载。
- Cookie仅隔离运行期；AppContainer实机音频E_ACCESSDENIED且IE/WinINet导航失败，不能因合成测试通过恢复该方案。持久隔离和新增系统用户等边界见CONTEXT。
- 游戏入口从官方页解析；sda主机曾对无Referer请求返回错误页，按同路径映射可直连主机。不能把某次游戏版本写成永久入口。
- 发布采用压缩自包含程序，不手工裁剪运行库，不启用WPF不兼容裁剪。恢复卡住游戏通过重启该账号，不能由此推断卡顿根因已解决。