# 实现地图

只描述当前实现；待做事项见 [backlog](backlog.md)，历史故障原因见 [回归要点](regressions.md)。

## 运行链

管理面板 → 每账号独立WPF容器 → 独立x86原生宿主 → Shell.Explorer.2 / 本机Flash ActiveX。
面板与容器由同一主程序启动；发布时压缩进自包含单文件，原生宿主始终独立。

| 修改目标 | 先读 |
|---|---|
| 脚本窗口与快捷方案 | Desktop/ScriptManagerWindow、ScriptWorkspace、MainWindow |
| 每账号默认日常、首次选档与模板升级 | Application/AccountDailyPlans、BuiltInScriptPlans、ScriptProgram；Desktop/SaveSlotSelectionWindow；Infrastructure/ScriptFileStore |
| 脚本定义/配置、中文解析与账号队列 | Application/ScriptModels、ScriptProgram、ScriptProgramRunner、ScriptCoordinator；Infrastructure/ScriptFileStore |
| 存档名区域/等级后缀与校正同步 | Runtime.Flash/SaveSlotNameReader、SharedScriptVision；Desktop/ScriptManagerWindow；Application/ScriptPlanTask |
| 会话定向控制及共享视觉 | Runtime.Flash/AutomationSessionPipe、OfficialScriptExecutor、SharedScriptVision；native/ScriptVision |
| 面板/账号编辑/组合根 | src/BqtjLauncher.Desktop/ |
| 账号与会话规则 | src/BqtjLauncher.Domain/、src/BqtjLauncher.Application/LauncherModule.cs |
| 明文凭据与事务 | src/BqtjLauncher.Application/IAccountEditor.cs、src/BqtjLauncher.Infrastructure/SqliteAccountEditor.cs |
| 账号管理、搜索/原位编辑/多选删除、批量格式与审核 | Application/AccountTransfer.cs、Infrastructure/SqliteAccountTransfer.cs、Desktop/AccountTransferWindow.xaml.cs |
| 独立工具入口与单实例唤起 | native/AccountTool/account_tool_launcher.c、Desktop/AccountToolInstance.cs、App.xaml.cs 的 --account-tool 模式 |
| 容器工具栏、加载层 | src/BqtjLauncher.Runtime.Flash/FlashHostWindow.cs |
| 首次透明显示、全屏激活约束 | src/BqtjLauncher.Runtime.Flash/GameWindowStartupDisplay.cs |
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
- 管道传凭据用限长UTF-16十六进制，防止分隔符注入；这是编码而非加密。凭据传输不进命令行、URL或日志，自动登录不通过剪贴板转移密码；账号管理工具按用户选择明文展示。
- 填充限定可信HTTPS文档、同表单字段和提交目标；保留用户输入，完成一次点击后不自动重试。
- 显示按原生客户区计算整数百分比和居中黑边；WPF负责窗口档位与加载层，就绪等待后固定1秒黑底。首次全屏显示保留最大化并允许激活，避免 WPF 的 ShowActivated=false 冲突；普通模式透明初始化时不激活。
- 游戏包装页按版本发布，入口地址是运行时数据：每次启动读取4399官方游戏页得到当前版本路径，换到可直连主机后交给原生宿主；官方页不可用时依次回退上次成功解析的缓存和随包兜底地址。
- 原生输入线程使用ReadFile；就绪后托管端由唯一后台任务读取stdout。需回执的操作串行且带请求编号，超时只结束请求等待，迟到回执按编号丢弃；凭据与resize仍为单向命令，共用写入锁。WPF只传实际客户区，原生端在resize、导航完成与show时校准。查询当前光学倍率，仅在不匹配时写入，减少闪帧。
- 原生x86宿主开启大地址支持，增加x64 Windows上的地址空间余量；刷新显式Stop导航、关闭旧OLE实例并销毁窗口，保留进程内Cookie。该改动不代表已修复游戏内部内存泄漏。
- Flash计时导入只在当前宿主内替换，连续结算后切换倍率；音频通过宿主PID控制。
- 共享偏好用命名互斥、临时文件原子替换及分字段更新，防止多开互相覆盖。

## 本地数据

`%LOCALAPPDATA%\BqtjLauncher`（当前 Windows 用户的本地数据目录）：

- launcher.db：账号记录及按Guid关联的明文凭据；创建/编辑用事务，删除账号级联删除凭据。
- runtime-preferences.json：共享窗口档位、静音。
- game-page.json：上次成功解析的游戏入口及时间，仅在读取官方页失败时用于回退。
- speed/：各账号上一档变速。
- logs/：运行日志，不得包含凭据。

AppContainer 兼容性类未接入生产链；已否决方案的独立启动程序及诊断入口已清理，失败原因保留于 [技术决策](decisions.md)。native/Diagnostics 保留当前回归工具，只使用合成数据。当前只保证已验证的运行时Cookie行为，不外推Flash全部本地存储。



## 技术选择

- WPF/win-x86承载本机Flash ActiveX；每账号独立原生宿主隔离故障。同进程多ActiveX曾使面板崩溃，不能退回该方案。
- 变速只替换当前宿主中Flash OCX计时导入，切换前按旧倍率连续结算；旧变速DLL进入.NET曾引发CoreCLR崩溃，不再加载。
- Cookie仅隔离运行期；AppContainer实机音频E_ACCESSDENIED且IE/WinINet导航失败，不能因合成测试通过恢复该方案。持久隔离和新增系统用户等边界见CONTEXT。
- 游戏入口从官方页解析；sda主机曾对无Referer请求返回错误页，按同路径映射可直连主机。不能把某次游戏版本写成永久入口。
- 发布采用压缩自包含程序，不手工裁剪运行库，不启用WPF不兼容裁剪。恢复卡住游戏通过重启该账号，不能由此推断卡顿根因已解决。
## 可选脚本链

脚本窗口与主面板共用LauncherModule，v2按账号ID取得已有或新建窗口的互斥控制权，v1保留等待规则。容器通过当前用户命名管道提供自身Flash子窗口、临时倍率及尺寸解锁；脚本创建时固定950×600物理像素，禁用尺寸菜单、最大化和拖拽缩放，跨DPI重新校准。成功、停止、失败的保留窗口统一解锁，不覆盖共享窗口偏好；不按桌面标题或名称猜测句柄，不持久写速度偏好。脚本窗口关闭只隐藏，队列由工作区管理。

单个x64原生视觉进程动态加载MaaFramework与CPU中文OCR，通过编号协议串行处理识别请求；每账号窗口保留独立截图缓存，同帧快照不可跨目标使用。只发送该Flash子窗口的后台消息，不移动系统光标。官方保存由内置脚本完成；本次进度增长、N/N标题及其左侧绿色独立勾形持续确认至少3秒后，才退出存档或精确关闭原会话。失败或取消保留窗口并恢复临时倍率。列表末行可能超出侧栏视口，完成判断只绑定整体标题，不要求末行可见。脚本管理窗口按所在显示器DPI转换工作区，确保标题栏与关闭按钮可见。

中文“读取存档名称”语句临时设2倍速，完成保留供根运行后续语句复用；旧临时扫描接口仍在成功后关闭自有窗口，失败/取消恢复原倍率。名称区域为卡片中心相对(-76,-17)的168×20像素行，黄色遮罩排除右上灰色通关标记和下方时间；同帧生成白底、黑底3倍图，按字符数加权OCR置信度选择，黑底提升至少0.02才替换默认白底结果。保留数字名称，只规范行尾lv等级。低置信度项提示校正。校正或重新读取按账号ID＋存档号更新编辑队列并通知绑定，不改变运行快照。该指令依赖组件protocolVersion=2，开始识别前校验，旧组件提示配套更新。

脚本定义与用户方案在程序目录scripts/，外部组件在components/automation/；两者不进入账号数据库。用户方案不含凭据，发布仅携带resources下的默认脚本，不复制运行目录中的用户配置。
v2中文编辑器以ScriptProgram编译显式顺序/并行及子方案调用树，校验重名绑定、调用环和并行账号交集。LauncherModule按稳定账号ID原子取得已有或新建会话控制权，普通容器也开放绑定面板PID的控制管道；同次运行保留已知档号，跨运行接管重新观察并回选档确认目标，不能信任用户手动操作前的旧档号。根运行持有会话，子方案之间复用，结束统一释放倍率、尺寸锁和视觉；v1仍走原语义。方案改名可选按ID同步调用文字，旧别名兼容；账号和常量绑定不进入凭据。保存后右栏切换监控，不把草稿当运行队列。方案文档允许无效语句草稿及constantsSource原文，执行编译才强制校验。内置模板通过EnsureBuiltInPlans在plans首次生成并保留标记，后续不覆盖编辑/删除；与用户方案同列表。源码头部方案设置同步isSubPlan/showOnDashboard，调用权限与QuickPlans可见性分开；旧方案默认可调用。全局常量在scripts/config/constants.txt，本方案同名覆盖，编译冻结后不影响本轮运行。QuickPlans只投影面板可见的方案，主面板另将DefaultAccountId绑定的日常放到当前账号入口，其他方案折叠；调用选择器只列出子方案。运行前先编译完整树，收集“待选择存档”，先复用/启动窗口自动读取名称，再弹两列四行选择，全部确认才回写源码；取消保留名称和窗口。名称资料在scripts/catalog，下次编译带入显示名称。本轮细节见[脚本设计](script-composition.md)。
