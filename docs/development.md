# 开发手册

## 环境与快速循环

Windows 10/11，global.json指定的.NET SDK，32位MinGW-w64 GCC。运行游戏需本机注册32位Flash ActiveX。
编译器按显式 -CompilerPath、BQTJ_MINGW32_GCC、PATH查找，勿把开发机路径写进代码。

```powershell
dotnet restore BqtjLauncher.sln
dotnet test BqtjLauncher.sln --configuration Release
./tools/Start-DevLauncher.ps1
```

开发启动脚本在 artifacts/dev 两个目录间轮换。构建、测试、发布会共用bin/obj，必须串行。

## 按影响选择验证

| 改动 | 必要验证 |
|---|---|
| 文案/文档 | 链接、过时状态、范围一致；不重建发行包 |
| 账号/存储/会话 | 相关xUnit；用临时库及虚构值，不读用户库作夹具 |
| 游戏入口版本解析 | `GamePageResolutionTests`（离线夹具）；改动镜像主机或页面解析时再用生产类跑一次实机网络探针 |
| 登录/管道 | native/Diagnostics 中对应探针；真实账号最终行为需实机证据 |
| 尺寸/加载/音频 | 相关逻辑或原生探针，再检查实际游戏窗口 |
| 打包/公共运行链 | 全量测试、x86构建、ZIP验证及从发布目录启动 |

总统权益由game_profiles.has_president保存，旧库初始化时同事务补列，默认关闭。新增/编辑账号和独立账号工具均可修改，三字段TXT导入不覆盖已有权益，启动时间回写也不覆盖；运行快照读取账号权益，修改下次新运行生效。验证用AccountEditorTests、ScriptProgramTests、DailyMaterialFarmingTests和OfficialScriptExecutorTests，界面用Test-AccountManager及Test-ScriptManagement；真实总统自动拾取仍需手动验收，操作差异见[材料刷取](sky-top-materials.md)。

回归源：LoginAutofillProbe.c、CredentialPipeProbe.c、ViewportProbe.c、StartupZoomProbe.c、LoadingZoomProbe.c、AddressSpaceProbe.c、NativeCommandPipeProbe.c、NativeCookieSessionProbe。
原因和验证入口见 [回归要点](regressions.md) 与 [诊断索引](README.md)。LiveLoginAutofillProbe使用虚构值，默认不提交网络登录。不要把启动成功代替具体行为验收。
原生宿主每次改动都要重编译；本机没有32位MinGW-w64时不得把“C#测试通过”当作原生已验证。

窗口与刷新离线验证：
```powershell
./tools/Test-NativeViewport.ps1 -CompilerPath <32位gcc路径>
```
该脚本串行验证居中布局、真实Flash像素缩放、倍率/导航重置恢复、延迟初始化及加载中切档、20次刷新、高位地址与空闲管道。像素测试短暂显示自己的无焦点夹具，不读取账号或加载游戏资源；中间产物仅写入artifacts/dev/probes。

快速复现加载缩放检查：运行上面的编译验证后，可单独执行 `./artifacts/dev/probes/LoadingZoomProbe.exe`，约7秒，三轮均断言实际红矩形像素；需要本机32位Flash。`ReadyState=4`不代表游戏脚本已初始化，静态单帧检查不能替代此检查。

NativeCookieSessionProbe按stage/ready协议等待启动，测试本地HTTP与唯一虚构Cookie。若沙箱写入合成Cookie报12004，需在允许写入测试Cookie的环境运行，不能当作产品会话隔离失败或跳过断言后宣称通过。

全屏首次显示回归：`dotnet test tests/BqtjLauncher.Application.Tests/BqtjLauncher.Application.Tests.csproj -c Release --filter FullyQualifiedName~GameWindowStartupDisplayTests`。
测试只创建并关闭自己的透明 WPF 窗口，不读取生产偏好/账号，不启动游戏；旧逻辑在最大化状态稳定抛出截图中的异常，修复后保留最大化并允许首次激活。运行偏好为全账号共享；修改此路径后，应重新验收实际游戏全屏退出后再启动的行为。
真实窗口回归放在非并发 `WpfUiTestGroup` 中，等待窗口/Dispatcher清理后才报告完成。CI另用独立testhost先运行GameWindowStartupDisplayTests，再在新进程执行其余.NET测试；非并发分组只能隔离同时执行，不能清除WPF进程级状态。30秒上限和显示断言保留，超时附固定阶段以区分创建、显示与清理；夹具上限不是产品启动指标。
命令恢复验证：`./tools/Test-NativeCommandRecovery.ps1 -CompilerPath <32位gcc路径>`。
该脚本连接生产托管通道与原生命令线程，用隐藏虚构窗口模拟主线程短时阻塞，验证超时、迟到回执、取消、旧协议与控制器关闭；实际匿名进程管道也必须结束。输出仅 artifacts/dev/probes/command-recovery，不创建账号或加载游戏。夹具未加载 Flash，速度错误回执是预期结果。NativeCookieSessionProbe首个账号使用带编号刷新，其余使用旧命令，均需保持合成 Cookie。

## 独立账号工具验证

`AccountTransferTests` 使用临时虚构数据库，覆盖格式解析、文件内重复、续行导入、审核前不覆盖、人工替换保留 ID/启动信息、并发写入去重、导出往返、原位编辑保留密码及选中记录级联删除。历史重复数据不会在初始化时删除或合并；若审核替换仍与另一条旧记录重复，用户须修改账号或在面板中手动清理。

`./tools/Test-AccountManager.ps1 -CompilerPath <32位gcc路径>` 编译并运行 `native/Diagnostics/AccountTransferUiProbe`。探针链接实际三个窗口和单实例协调器，使用自己的 Application、唯一事件命名空间及虚构库，不调用生产启动入口。它验证原生/托管唤起、次进程退出、最小化及最大化恢复、新增/编辑入口、导入续行、人工审核、三列原位编辑/取消/重复回滚、名称/账号搜索、多选实际删除，以及默认/最小窗口布局与滚动条。面板 XAML 仅绑定虚构数据作布局检查，不初始化生产服务。

结果与图片只写 `artifacts/dev/probes/account-manager-ui-*`，入口二进制放在带空格的 `account manager ui` 目录。删除确认自动操作只匹配探针自己的进程；环境变量结束后恢复。该脚本已接入 Windows CI，远程结果仍需实际运行确认。UI 探针不是发行程序，不得拿它启动游戏。

独立入口 `BqtjAccountTool.exe` 与主程序同目录，只传 `--account-tool`；该模式不创建面板、游戏或生产日志；当前 Windows 会话通过互斥锁保证一个工具，命名事件只通知激活，不传凭据。构建脚本和完整/覆盖包清单均检查三个 x86 入口；运行库仍只有主程序内的一份。

## 脚本开发与验证

脚本初版59a3c9b保留在codex/script-management，后续开发基线按用户要求推送dev，源自dev/a6246f8；只选择性迁入Maa的会话、视觉和完成判定，不恢复旧四账号上限或旧缩放实现。

运行 ./tools/Test-ScriptManagement.ps1 -Compiler64Path <x64-g++路径>，准备固定Maa 5.14.2及PP-OCRv4模型，构建CPU原生组件、执行自有窗口的后台识字/模板/点击/身份/尺寸/退出检查，再运行真实WPF脚本管理窗口和托管原生协议回归。中间输出只在artifacts/dev/probes。没有生产App、账号库或游戏加载；虚构库和图片不得交付。

-UiOnly仅检查窗口和队列交互，不替代视觉验证。-AllowNonInteractive用于CI无交互桌面；若系统光标无法读取，会明确标注该断言未执行，不能代替正常桌面的鼠标/前台保持证据。

./tools/Build-ScriptComponent.ps1 -Compiler64Path <x64-g++路径> 只输出artifacts/dev/components/automation；默认使用本机x64 v14运行库，可通过 -VcRuntimeDirectory 指向Visual Studio Redist目录。组件独立于x86主程序，不携带另一份.NET；CPU推理仅交付必需动态库、中文模型和许可证。

完整主程序开发ZIP使用原Publish-Release.ps1 -Development命令；独立组件使用 ./tools/Publish-ScriptComponent.ps1 -Version 0.0.0-dev.<UTC时间戳> -Compiler64Path <x64-g++路径>。组件ZIP直接解压到主程序目录，得到components/automation，不带版本外层。存档名要求protocolVersion=2，已有协议2组件可沿用；刷材料要求协议7，新增同帧蓝/绿色小计数识别，并包含后台悬停、WASD斜向短按、具名N键AI切换及同帧多目标识图。普通移动接口仍不允许N，取消须等待松键；开始刷材料前拒绝旧组件。覆盖更新保留scripts/plans、config、catalog和用户脚本，默认资源随主包交付；完整及覆盖包清单校验默认资源，组件另检x64及SHA256。

ScriptManagementTests覆盖配置、排序、并发、已有窗口等待、关窗时机、失败隔离、取消/恢复倍率、2倍速扫描及失败后恢复；ScriptProgramTests验证中文CSV/全局默认常量及本方案覆盖、头部声明与调用权限、引用更新、顺序/并行、同次窗口复用、失败和停止、普通窗口接管及指定档切换、跨运行复用与控制互斥。AccountDailyPlanTests验证默认日常按账号ID初始化、删除不重建、首次选档回写、子方案重复调用及模板v2迁移；真实WPF探针另验证2×4选档布局、首次自动读名、取消保留扫描窗口及名称且不改写方案、全部子方案确认后回写、下次免询问和复制不占默认入口。编辑/监控模式在右下角分别显示插入/停止，账号工具默认隐藏密码列并可勾选显示编辑。真实WPF探针验证统一可编辑内置模板、双击插入/编辑、错误语句及常量原文保存而不执行、头部面板可见性、空行/非空行插入、保存确认的三种选择、全局常量保存、名称校正、改名同步、保存切换监控、隐藏不停止及默认/最小布局。ScriptProgramTests另覆盖模板一次初始化和编辑/删除后不重置，以及旧文档默认可见。SaveSlotNameReaderTests覆盖数字名称、等级后缀、对比度选择及旧协议拒绝；OfficialScriptExecutorTests用合成菜单与像素覆盖标题先于卡片出现时的等待/取消且不重发点击、八档映射、真正的日常流程、标题整体完成绿勾观察（末行不在视口内）及退出确认。OfficialCompletionVisionTests排除其他行勾、红勾、斜线、叉号和纯色块。Domain完成判断回归覆盖旧画面、证据过期、进度变化与持续3秒。实际OCR另用用户授权的保留选档画面作只读比对，不能外推其他字体、等级或全部场景。真实Flash后台兼容、跨屏DPI及两个账号连续多档仍由开发包人工验收，不以合成结果代替。
只读现场回放：先在用户授权的游戏侧栏截图与对应Maa OCR JSON上运行 dotnet run --project native/Diagnostics/ScriptCompletionProbe/ScriptCompletionProbe.csproj -- <侧栏PNG> <OCR-JSON>。它只读取这两个文件并报告标题绿勾，不发送点击；静态截图没有本次进度增长证据，不能用它触发关窗。

FarmingSceneProbe的`--system`只读采样执行器实际使用的系统面板区域（90,20,760,510），保存局部截图及OCR坐标；`--daily`按日常侧栏原区域连续采样四帧。局部OCR必须按真实ROI复测，不能用全图结果或合成文字代替。日常后接刷关另有过渡空帧、系统面板、官方侧栏遮挡地图和侧栏收起延迟的执行器回归，实际后台点击仍须用开发包验证。

`--mode`只读采样正常/挑战菜单的实际ROI（260,140,440,140）。正常模式卡住时先比对这个区域，再查脚本日志；识别成功和原生点击回执都不能单独证明难度页已出现。

`--save-title`先纯截图，再对同一快照按选档标题ROI（240,135,480,65）识字，分别报告截图与识别结果；保存局部PNG/OCR，不点击读取存档。用于首次观察失败时区分截图和OCR，空白区域应正常返回空结果。脚本日志的initial-observation-retry表示首次尚未取得画面时的一次本窗口重连；新组件将原生失败分为controller-connect、capture、recognition，仍不记录任意OCR正文。

脚本日志由ScriptRunLog写入`%LOCALAPPDATA%/BqtjLauncher/logs/script-日期.jsonl`，每行独立JSON，按天及2MiB滚动，最多7份。只记录版本、运行/会话随机编号、固定菜单词/坐标/置信度、快照编号/年龄、点击发送与回执、等待/超时及错误类型，不记录账号、方案原文、未知OCR或截图。相同观察每5秒留一次、变化立即记录；目标不可用只记进入与恢复。用户授权后可以直接读取该脚本日志，其他生产数据读取限制不变；当前“导出诊断包”仍只导出通用日志白名单摘要，不自动打包这些JSON日志。

选档名称只读探针：构建native/Diagnostics/SaveNameProbe后，传入明确的容器PID、原生宿主PID、artifacts/dev/probes下的输出目录及组件目录。它只使用定向Flash后台截图，保存选档卡片区域及OCR对比，不发送点击、不激活/调整窗口；只在用户授权时运行，不读取账号库或生产日志。真实角色名称证据留在忽略目录，不加入源码或交付。管理页检查继续只用虚构账号。

组件ZIP根固定为components/automation，直接解压到EXE所在目录。运行 ./tools/Test-ScriptUpdate.ps1 -LauncherArchivePath <完整开发包ZIP> -ComponentArchivePath <组件ZIP>，在虚构安装目录验证直接解压路径及重复合并更新保留用户方案、全局常量、默认日常初始化记录、模板初始化标记、名称资料和自定义脚本。它不运行游戏或读取用户配置。

界面静态文字在XAML，动态文字在C#，具体位置和预览方法见[文案修改](ui-text-editing.md)。

## 构建与交付

先阅读 [构建与版本规范](build-policy.md)，这是目录、版本和人工验收门槛的唯一规范。

```powershell
./tools/Start-DevLauncher.ps1 -BuildOnly
$devVersion = '0.0.0-dev.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
./tools/Publish-Release.ps1 -Development -Version $devVersion
# 开发覆盖更新包：在上一条命令加 -HotUpdate；仍只输出 artifacts/dev。
# 正式包仅在本次用户验收并下令后：
# ./tools/Publish-Release.ps1 -ReleaseApproved -Version <已核对的新正式版本>
```

完整包包含三个入口程序（面板、原生宿主、独立账号工具）、README、GPLv3 许可证、第三方声明和默认脚本资源；覆盖更新包包含三个入口程序、HOTFIX-README.md和默认脚本资源，原安装目录的许可证及第三方声明必须保留。两种包均校验清单、x86入口及 SHA256，不分发 Flash 或用户数据。构建配置 Release 不代表允许正式发行；是否正式交付由模式与人工门槛决定。
若使用 -NoRestore 且提示缺少win-x86资产，先运行：
```powershell
dotnet restore src/BqtjLauncher.Desktop/BqtjLauncher.Desktop.csproj -r win-x86 -p:PublishSingleFile=true
```

## 图标与清理

```powershell
./tools/New-LauncherIcon.ps1
./tools/Clear-GeneratedFiles.ps1
```

原图保存在Desktop/Assets/icon-source.jpg。清理仅处理仓库生成目录，默认保留全部开发验收包和发行包，跳过运行中产物、疑似用户数据和重解析点；不要改用全仓库删除命令。bin/obj清理后需重新restore。

用户要求清理旧包时，单独核对过期版本及其 ZIP、SHA256、解压目录与验收记录，再按仓库内绝对路径删除；保留当前验收包及最新正式交付包。隔离方案原型、旧固定游戏地址 UI 脚本及第三方变速 DLL 探针已退出当前工具集，历史原因留在技术决策与回归文档；不要恢复这些入口代替现有验证。

## CI与提交前检查

CI在main、dev及codex/*分支推送、PR和手动触发时运行。所有改动先在Linux检查Git清单和Markdown相对链接，拒绝生成包、账号库、日志及密钥进入源码。只有产品源码、测试、原生代码、非文档资源/工具或构建配置变化才进入Windows验证；纯文档不安装.NET、MinGW或视觉依赖。工作流变动另跑actionlint，手动运行强制完整验证。

Windows仍串行执行格式、全量.NET测试、命令恢复、账号/脚本窗口及视觉夹具，再生成开发ZIP。NuGet包和固定Maa/OCR依赖分别缓存，运行会话、用户配置和现场不缓存。重复推送取消旧运行；开发包保留7天，正式发布门槛不变。

本地提交前运行 `./tools/Test-Repository.ps1 -FullVerification`、`dotnet format BqtjLauncher.sln --verify-no-changes --no-restore`，再按改动范围选择上方检查。仓库检查只处理Git清单，因此新增源码和文档先暂存再检查，提交前重新查看暂存范围；不要暂存整个运行目录。
## 远程交付

本地ZIP生成与公开发布是不同操作。只有用户要求公开时，才处理许可证、提交范围和远程推送；不为普通本地开发添加发布审批。
CI工作流覆盖测试、原生构建与ZIP校验；仅在本次人工验收和发布授权后推送标签；标签工作流先上传Release草稿，成功后自动公开；必须提供docs/release-v版本号.md，已公开版本不覆盖。是否实跑成功必须有托管结果，不能以“配置存在”当作通过。


## 自动构建开发预览

双击 tools/Start-DevPreview.cmd；编译器由 BQTJ_MINGW32_GCC 或 PATH 提供，也可将32位gcc路径作为第一个参数。清理后本地快捷入口需按需重新生成（不入库）。

保存源码后等待2秒稳定期，自动构建空闲槽位，成功后重启由该监视器启动的开发面板。游戏会中断；其他手动启动窗口和正式包不自动关闭。失败保留旧窗口；修复并保存后重试。Ctrl+C停止监视，当前预览继续运行。原生Flash不能在保留关卡状态的同时热替换，这里采用自动重建/重启。监视期间不要另行运行测试或发布，以免争用bin/obj。

手动调用：./tools/Watch-DevLauncher.ps1 -AutoRestart -CompilerPath <32位gcc路径>；仅构建一次使用 -Once，不带 -AutoRestart。成功标记防止失败构建被 -NoBuild 选中。

## 内存观察

运行 ./tools/Measure-GameMemory.ps1，默认每2秒采样、共30次；只记录仓库内进程的PID、私有提交量、工作集和句柄数。不读账号和内存内容。比较进入游戏、切关、刷新及重启前后的私有提交量，不能以一次工作集下降判断泄漏已修复。

Flash进程内存不受.NET垃圾回收管理。重启单个账号容器可以释放其进程资源但会中断关卡；不自动触发GC、清Cookie或修剪工作集。

## 用户反馈与诊断工具

面板右上角提供“打开日志目录”和“导出诊断包”。用户选择 ZIP 保存位置后，后台导出版本、系统、运行库、架构及最近最多 7 份日志摘要，每份只取末尾 2MiB。没有日志时仍可导出环境信息，不可读文件只记录跳过数量。

摘要只保留时间、级别、固定事件、错误类型和有限数值，不原样复制异常正文、URL、账号信息或完整日志，不读取账号库或 Cookie，不自动上传。反馈应附版本、操作步骤和发生时间，必要时用户自行提交诊断包；不得上传整个用户数据目录。

原始日志位于 `%LOCALAPPDATA%/BqtjLauncher/logs`，按天或 5MiB 滚动，最多保留 7 个文件；面板与容器使用共享写入。摘要会舍弃部分细节，不能视为完整转储。诊断导出回归仅使用临时虚构日志，agent 不打开或导出用户真实日志作为测试材料。

DailyMaterialQuotaTests覆盖宝石共享别名（含实机守护宝石独立无计数行）与冥王图鉴双上限、缺项/冲突/歧义计数和绿色证据；DailyMaterialFarmingTests用受控适配器验证第10轮起检查、固定次数、拾取失败不重玩和停止保留。ScriptProgramTests覆盖短式/显式语句、串行上下文和继承选档回写。真实WPF探针验证刷关表单默认值及未校准能力在任何游戏操作前拒绝。SkyTopSceneVisionTests覆盖HUD连写、唯一角色、箭头形状及青色/绿色区别；FarmingSceneProbe可对授权950×600现场只读采集，或用--offline <PNG> <OCR-JSON>回放。协议4夹具还检查W+D同时按下、双键释放、非法相反方向拒绝及取消后可继续识别。SkyTopSceneVisionTests用纯图标夹具回放装备/武器品质光效、三个相邻枪械、材料/宝箱及副手/装置分类；整幅用户截图不入库。FarmingSceneProbe的--offline会输出分类标注图和本次扫描耗时，只观察、不操作游戏。上述检查不外推未覆盖的天气、关卡或账号；天空顶已由用户确认为初代可用，剩余实机范围见[材料刷取](sky-top-materials.md)。

独立脚本资源包使用`./tools/Publish-ScriptResources.ps1 -Version 0.0.0-dev.<UTC时间戳>`，ZIP根scripts，只包含源码中的官方JSON、说明和resources/loot图标规则。适用于已支持外置图标的本轮开发主程序；不能给v0.1.5补出新引擎。覆盖更新回归可再传`-ScriptArchivePath <脚本包ZIP>`，同时验证资源替换和用户方案保留。视觉组件仍单独发布，ZIP根components/automation。

FarmingSceneProbe的`--motion <前帧PNG> <后帧PNG>`只读回放前景配准数值，不输出角色名。无交互桌面、窗口关闭或PID不匹配时返回诊断失败，不抛出未处理异常触发Windows弹窗；此工具不进入发行包。拾取的CaptureAsync复用协议4已有pixels新快照，托管桥接夹具覆盖编号、时间和完整像素，不调用OCR。

`--scores <PNG> <x> <y> <宽> <高>`只读比较指定区域内冷冻宝石、守护宝石、图鉴各模板的最高相关度和锚点误差，区分模板样式缺失与锚点筛选失败；不调整阈值、不操作游戏。小雨回归使用匿名12帧掉落堆和同场背景裁剪，误拾取回归保留匿名蓝色装置、出口光效及角色喷焰；大雨需要另有现场验证。
