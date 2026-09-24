# 实现地图

只描述当前实现；待做事项见 [backlog](backlog.md)，技术选择见 [当前决策](decisions.md)。

## 运行链

管理面板 → 每账号独立WPF容器 → 独立x86原生宿主 → Shell.Explorer.2 / 本机Flash ActiveX。
面板与容器由同一主程序启动；发布时压缩进自包含单文件，原生宿主始终独立。

| 修改目标 | 先读 |
|---|---|
| 面板/账号编辑/组合根 | src/BqtjLauncher.Desktop/ |
| 账号与会话规则 | src/BqtjLauncher.Domain/、src/BqtjLauncher.Application/LauncherModule.cs |
| 明文凭据与事务 | src/BqtjLauncher.Application/IAccountEditor.cs、src/BqtjLauncher.Infrastructure/SqliteAccountEditor.cs |
| 容器工具栏、加载层 | src/BqtjLauncher.Runtime.Flash/FlashHostWindow.cs |
| 启动/回执/生命周期 | FlashGameRuntime.cs、NativeFlashHostController.cs（同上目录） |
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
- 读取线程使用ReadFile，需回执的操作串行；刷新后恢复尺寸。光学倍率不变时避免重复调用，减少闪帧。
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


