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

回归源：LoginAutofillProbe.c、CredentialPipeProbe.c、ViewportProbe.c、NativeCommandPipeProbe.c、NativeCookieSessionProbe。
原因和验证入口见 [回归要点](regressions.md)。LiveLoginAutofillProbe使用虚构值，默认不提交网络登录。不要把启动成功代替具体行为验收。
原生宿主每次改动都要重编译；本机没有32位MinGW-w64时不得把“C#测试通过”当作原生已验证。

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

完整包包含两个入口程序、README 和第三方声明；覆盖更新包包含两个入口程序和 HOTFIX-README.md。两种包均校验清单、x86入口及 SHA256，不分发 Flash 或用户数据。构建配置 Release 不代表允许正式发行；是否正式交付由模式与人工门槛决定。
若使用 -NoRestore 且提示缺少win-x86资产，先运行：
```powershell
dotnet restore src/BqtjLauncher.Desktop/BqtjLauncher.Desktop.csproj -r win-x86 -p:PublishSingleFile=true
```

## 图标与清理

```powershell
./tools/New-LauncherIcon.ps1
./tools/Clear-GeneratedFiles.ps1
```

原图保存在src/BqtjLauncher.Desktop/Assets/icon-source.jpg。清理仅处理仓库生成目录，默认保留全部开发验收包和发行包，跳过运行中产物、疑似用户数据和重解析点；不要改用全仓库删除命令。bin/obj清理后需重新restore。

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

## 原生回归命令

在仓库根目录、已配置32位MinGW的终端执行；输出统一放 artifacts/dev/diagnostics。仅按改动选择探针，不批量运行全部历史诊断。公开网页探针不代表真实登录通过。

```powershell
New-Item -ItemType Directory -Force artifacts/dev/diagnostics | Out-Null
gcc -DUNICODE -O0 -o artifacts/dev/diagnostics/NativeCommandPipeProbe.exe native/Diagnostics/NativeCommandPipeProbe.c native/FlashHost/virtual_clock.c -lole32 -loleaut32 -luser32 -lgdi32 -lshell32 -lwininet -luuid
./tools/Test-NativeCommandPipe.ps1 -ProbePath artifacts/dev/diagnostics/NativeCommandPipeProbe.exe
gcc -DUNICODE -o artifacts/dev/diagnostics/ViewportProbe.exe native/Diagnostics/ViewportProbe.c native/FlashHost/virtual_clock.c -lole32 -loleaut32 -lshell32 -lwininet -luuid -lgdi32
./artifacts/dev/diagnostics/ViewportProbe.exe
gcc -DUNICODE '-Wl,--disable-nxcompat,--disable-dynamicbase' -o artifacts/dev/diagnostics/StartupZoomProbe.exe native/Diagnostics/StartupZoomProbe.c native/FlashHost/virtual_clock.c -lole32 -loleaut32 -lshell32 -lwininet -luuid -lgdi32
./artifacts/dev/diagnostics/StartupZoomProbe.exe
```

StartupZoomProbe需要本机32位Flash，使用与生产一致的链接选项；不能仅检查空白浏览器倍率替代Flash内容尺寸验证。F3改动需用户在游戏画面、工具栏及切换窗口后分别实测。
## 脚本组件开发准备

在Windows x64 PowerShell 7独立进程中运行以下命令，不在x86启动器内加载原生视觉库：

```powershell
./tools/Prepare-MaaFramework.ps1
./tools/Test-MaaRuntime.ps1
# 离线使用官方ZIP时，两条命令均可加 -ArchivePath <官方ZIP路径>。
```

准备脚本固定MaaFramework v5.14.2官方Windows x64资产及SHA256（来自官方Release资产元数据），下载、解压和来源标记仅写入 `artifacts/dev/automation/`，不会安装系统组件或修改正式包。已有目录不覆盖；重复准备仅验证ZIP和完成标记。中断产生的 `.partial` / `extract-*` 目录保留供排查，后续按构建规范清理。

Test-MaaRuntime在加载前逐文件核对官方ZIP与解压内容，再加载Framework、Toolkit和Win32控制库，检查版本及所需C接口；不枚举窗口、不截图、不创建控制器、不发送输入、不读取用户数据。PASS仅表示依赖及接口可用，不代表Flash后台截图、点击、OCR或完整脚本运行通过。

2026-09-30已验证：官方ZIP校验、独立x64库加载/接口、重复准备及合成错误ZIP拒绝。当前准备工具不随发行包分发；后续组件分发保留上游许可及第三方声明，不直接把完整SDK当作最终运行包。

合成窗口后台探针（会短暂显示一块测试色块，不打开游戏）：

```powershell
./tools/Test-MaaBackground.ps1 -CaptureMethod PrintWindow
./tools/Test-MaaBackground.ps1 -CaptureMethod FramePool
# 嵌套子窗口输入回归（不控制游戏）：
./tools/Test-MaaBackground.ps1 -CaptureMethod DirectChild
# 纯合成图片识别回归（不发送窗口输入）：
./tools/Test-MaaBackground.ps1 -CaptureMethod Template
```

运行器先校验依赖，再将 `native/Diagnostics/MaaBackgroundProbe` 构建到 `artifacts/dev/automation/background-probe`。测试只控制自己创建的两个色块窗口，用第三个窗口遮挡它们，核对目标颜色、指定位置点击计数及点击后新画面；在异步操作期间采样前台窗口与鼠标位置，变化则失败（用户移动鼠标也会使测试失败）。截图尺寸按创建后的实际客户区验证，不能假定Windows一定采用请求宽度。

2026-09-30两种截图方式配合PostMessage均通过：A/B各收到一次点击，遮挡窗口未收到点击，颜色更新正确，采样未发现焦点/鼠标变化。两控制器同时存在但动作依次发送，这不是并发负载测试；普通WinForms窗口通过也不证明Flash/ActiveX兼容。

每次结果写入 `artifacts/dev/automation/synthetic-<随机ID>/result.json`，仅含合成测试数据。内部操作超时8秒，外部进程硬超时45秒；超时只终止本次探针进程树。测试窗口在正常结束后关闭，不遗留常驻服务。此独立诊断项目未加入产品解决方案，相关改动需单独构建和格式检查。

真实Flash验证窗口：

```powershell
./tools/Test-MaaBackground.ps1 -InspectorBuildOnly
# 随后手动打开 artifacts/dev/automation/background-probe/MaaBackgroundProbe.exe。
```

先手动进入存档，再选宿主、浏览器或Flash子窗口，确认“目标已进入游戏，非登录页”。选PrintWindow/FramePool截图、PostMessage/SendMessage/DirectChild输入；DirectChild只允许Flash ActiveX，使用有界SendMessageTimeout直接发送移动、按下、释放，不发激活消息、不移动系统光标。每条消息最多等1秒，按下结果不确定也尝试释放，不自动重试；返回不代表游戏已响应。后台截图后在预览里选无副作用菜单按钮，再显式点击“后台单击一次”。坐标最多保留120秒，仅供人工诊断；一次点击后必须重新截图。目标或方式变化会清空预览；每次检查句柄、进程归属、宿主祖先、客户区尺寸和窗口可见性，拒绝最小化或隐藏窗口，不自动激活游戏。预览仅驻留内存，不保存真实游戏截图；第三方运行日志仅留在dev下，不读入或导出诊断结果。Maa操作软超时8秒、探针硬超时15秒，销毁控制器硬超时5秒；超时只退出探针，不结束游戏。

2026-10-03实机结果（单账号、游戏窗口被探针遮挡）：

| 目标与方式 | 观察结果 |
|---|---|
| Flash ActiveX＋PrintWindow | 正确取得1419×896游戏画面，客户区尺寸一致，未检测到前台焦点变化 |
| Flash ActiveX＋PostMessage / SendMessage | “助理”位置单击请求返回成功，未检测到焦点/鼠标变化；重新截图菜单未打开，输入验收失败 |
| 有效浏览器子窗口＋PrintWindow / SendMessage | 取得1416×894画面；“助理”单击后菜单仍未打开 |
| 另一浏览器子窗口 | 无有效客户区，停止操作，没有发送输入 |
| Flash ActiveX＋PrintWindow / DirectChild | 助理菜单后台打开及关闭成功，新截图确认主界面恢复，未检测到焦点/鼠标变化 |
| TemplateMatch＋DirectChild | 新截图中唯一识别到54×54助理按钮模板，自动后台打开菜单；菜单遮住按钮后再识别拒绝发送输入 |

官方v5.14.2的 [MessageInput实现](https://github.com/MaaXYZ/MaaFramework/blob/v5.14.2/source/MaaWin32ControlUnit/Input/MessageInput.cpp) 在get_active_hwnd中使用GA_ROOTOWNER/GetLastActivePopup，返回可见且不等于目标的窗口作为输入目标；根窗口自身也满足这个条件。本项目Flash嵌在WPF容器内，源码与直接子窗口成功的对照强烈支持顶层路由导致原输入无效的判断；未通过读取第三方日志确认实机路由。无需先假定Flash要求真实光标或激活状态。

模板识别操作：截图→在预览中选按钮中心→“记住按钮图像”→“识别并后台单击”。只保留按钮局部图像于内存，关闭探针即释放；切换目标可以复用图像，但每次重新截图、检查目标归属及尺寸。TemplateMatch阈值0.92，只有一个匹配才允许输入；识别与发送前要求截图不超过2秒，不使用人工选点的旧坐标。模板随视口等比缩放，比例明显变化时拒绝；实际100%/150%及多DPI适配仍需实机验收。当前仅是探针临时学习模板，不是可分发脚本资源，也不自动证明菜单已打开；操作后更新预览供检查。

合成Template回归已通过：模板位置改变后重新定位、缺失/重复目标拒绝、两个独立识别器并行处理不同画面且各自返回正确坐标。资源和图片只在内存中，识别缓存每轮清理，不加载OCR模型。

DirectChild合成回归已通过：A/B为两个独立容器中的子窗口，均被第三个窗口遮挡；先分别点击，再并行点击，各共收到两次按下、释放，父容器/遮挡窗口点击数均为0，前台焦点与光标检查通过。沙箱不能读取交互桌面光标时会失败退出，应在正常桌面会话运行，不能移除检查绕过失败。合成并行结果不替代真实双账号验收。

2026-10-03双窗口实机验证：两个账号均已进入存档且未最小化，按PID与Flash子窗口绑定分别截图。首个窗口打开助理后，第二个仍为地图；第二个独立采样后识别并打开助理，再关闭第一个，第二个助理仍保持打开。最后分别关闭菜单，两游戏恢复地图并保留运行，关闭探针。输入期间未检测到鼠标或前台焦点变化。动作串行，不代表真实并发负载验证。第二个窗口最小化时暂停并等待用户手动恢复，没有强制激活。

首个窗口的54×54模板在第二个未匹配，工具拒绝发送输入；分别采样后两个窗口均通过。通用脚本素材必须进一步验证动态效果、取样区域及不同账号差异，不能通过降低阈值直接宣称兼容。

完成判定模块 `src/BqtjLauncher.Domain/DailyScriptCompletionGate.cs` 已实现，通过DailyScriptRunObserver接入开发探针OCR，尚未接入产品。每次运行使用独立Guid；先观察到未完成的运行进度实际增长，随后要求N/N与最终保存绿勾连续稳定成立。样本数、稳定时长、最大帧间隔均显式配置，不设未经实机验证的正式默认值；使用真实单调时钟。过期、重复、乱序或证据丢失会撤销确认，其他运行观测被拒绝，中间红叉仅保留提示。VisualComplete只代表画面证据，不触发自动关闭，也不证明服务端保存成功。

```powershell
dotnet test tests/BqtjLauncher.Domain.Tests/BqtjLauncher.Domain.Tests.csproj --no-restore --nologo -p:NuGetAudit=false
```

完成判定测试17项、进度文字解析16项、连续观测9项通过，领域共55项通过。覆盖旧完成/静止未完成画面、进度增长、红叉、保存证据丢失、时间过期/乱序和运行身份隔离；已开始后进度倒退或总数变化拒绝，缺帧不能掩盖重新运行。测试使用虚构观测与文字，本轮未运行全量产品测试；已有NuGet审计联网警告不代表代码失败。

中文OCR准备与纯图片验证：

```powershell
./tools/Prepare-MaaOcr.ps1
# 离线来源包含det.onnx、rec.onnx、keys.txt、README.md：
# ./tools/Prepare-MaaOcr.ps1 -SourceDirectory <官方文件目录>
./tools/Prepare-MaaOcr.ps1 -VerifyOnly
./tools/Test-MaaBackground.ps1 -CaptureMethod OCR
```

模型来自 [MaaCommonAssets固定提交](https://github.com/MaaXYZ/MaaCommonAssets/tree/dabcd4681ac990dc4361de26416d986abd80e4aa/OCR/ppocr_v4/zh_cn)，四个文件已与官方Git blob核对，SHA256唯一清单在tools/maa-ocr-models.json。准备到artifacts/dev/automation/ppocr-v4-zh-cn，已有目录只校验不覆盖，拒绝重解析路径。探针嵌入同一清单，加载前再次校验，不信任可修改的完成标记。模型与准备工具均不进入发行包；实际分发前仍需整理上游许可。

2026-10-04合成OCR通过：中文“日常操作复制”“保存存档”、3/22→35/35新帧更新、空白图片无旧结果、双独立实例并行不串进度、缺失/损坏模型拒绝。OCR及模板回归构建零警告/零错误，格式检查通过。OCR不绑定输入控制器，识别缓存每轮清理，图片驻留内存；这里只证明模型可工作，不证明游戏字体、侧栏或保存成功可识别。

人工只读识别入口：先进入存档，选择目标并确认非登录页→后台截图→在预览选择脚本侧栏一角→“设为文字区域起点”→选择另一角→“只读识别文字区域”。仅选择官方脚本侧栏，不选择账号信息。模型加载后重新截图，尺寸/归属/可见性/前台焦点变化或结果超过2秒即拒绝；不自动激活、不发送输入、不存图或导出原始文字。结果仅在探针窗口显示，选择区域随截图或目标切换清除。

DailyScriptProgressParser只接受唯一完整标题及合法数字，支持空白/全角字符，拒绝重复标题、残缺行、OCR数字猜测与溢出。真实OCR曾把标题勾形读成斜杠，现只允许其作为标题前单个装饰，不推断保存状态。OCR置信阈值0.85仅为探针值，真实侧栏准确率待继续验收。“保存存档”文字与标题勾号不是最终保存证据，不据此执行关闭动作。

GreenScriptVision接通原图定位唯一保存行→同一帧局部绿色过滤/放大OCR→完整保存文字与独立连通勾形匹配。原图的“保存存档”混读只用于定位，不作完成证据；缺失或重复行不猜测位置。过滤条件G≥70、G−R≥40、G−B≥35，三倍放大；形状检查短下行及长上行，排除横线、单斜杠、叉号、色块及汉字笔画。OCR检测框可能包含但文字省略勾号，搜索可扩至框内一个字符高度；小字号左端衬线采用拐点前局部中心，坐标以笔画自身顶部归一化。参数仅在当前探针中验证，不能承诺全部字体/DPI兼容。形状诊断只显示计数、尺寸和列中心，不落盘真实像素或原始文字。

合成OCR回归额外通过：白色/黄色背景过滤、自动定位混读行后重新识别保存文字、缺失/红色勾/斜杠/叉号/色块/重复目标拒绝、绿色汉字无勾不误判、小字号衬线勾形、OCR框含勾但文字省略。连续观测复用同一识别函数后，原有OCR测试命令仍通过；未构建产品包，最新探针构建零警告/零错误。

2026-10-04只读实机结果：用户手动运行内置脚本后保留完成画面；PrintWindow取得950×600画面，手动侧栏区域(764,162,181,412)中，同一张新截图解析22/22，并自动定位末行、过滤背景数值及匹配独立绿色勾形。单独末行区域(763,545,101,29)也通过。未降低OCR阈值，没有把OCR勾字符当形状证据。顶栏随内容增高，当前DPI按钮完整可见。仅操作探针，没有游戏输入、存档修改或游戏关闭；验证后关闭探针，保留用户现场。未保存真实截图或原始文字，不记录账号信息。此处仅为单帧证据；后续连续过程与人工保存验证见下文，无勾/等待/失败真实画面及多比例/DPI仍待验收。

连续只读入口：按上述方式限定完整侧栏，点击“连续只读观测”，再由用户手动开始一次官方脚本。模型复用，约每秒采样；探针要求至少3个保存样本、持续至少3秒、帧间隔不超过5秒，最多观测60分钟。这些仅为开发探针参数。旧N/N或静止的未完成进度保持AwaitingStart；必须看见同总数的进度增长且仍未完成，因此直接从0/N跳到N/N（包括单步脚本）不能确认开始。开始后的倒退/总数改变停止观测，缺帧撤销保存稳定确认。目标归属、尺寸、可见性或截图新鲜度检查失败即停止；观测中仅允许停止，关闭探针先取消观测，不发送游戏输入、不关闭游戏。每轮原生调用15秒硬截止，OCR销毁5秒硬截止，只结束探针。

2026-10-04连续观测实机结果：用户解锁后，真实950×600主界面已收起脚本侧栏；选择区域(763,158,182,416)，无进度及保存证据时连续保持AwaitingStart，手动停止正常恢复控件、保留游戏。首次手动运行后，Computer Use读取到的可访问性文字仍为AwaitingStart；后续发现其文字可能滞后于实际截图，首次结果未经内存历史确认，不能据此认定业务判定失败。

开发探针新增最近12次结构化变化摘要（帧号、真实秒数、判定阶段、标题候选数量、已解析数字进度、保存勾形布尔值），只驻留内存，不存原始OCR文字或截图；每次启动观测清空摘要，停止后保留供检查。构建零警告、格式检查通过。新探针在区域(759,154,182,420)的旧完成画面连续23帧保持AwaitingStart，识别22/22与保存绿勾而不误判。用户再次手动运行后，第140帧/227.2秒进入Running（2/22），随后4/22、7/22、8/22、9/22、21/22；第162帧/268.1秒ConfirmingSave（22/22及保存绿勾），第164帧/271.3秒VisualComplete并自动停止，实际稳定约3.2秒。运行中间有无法解析帧，不猜数字；中间红叉仅提示，未阻断最终完成。探针时间包含开始前等待，不等于脚本耗时。Computer Use可访问性文字曾滞后，刷新探针布局后内存摘要与截图一致，实机结论以相互核对后的结果为准。未发送游戏输入或关闭用户游戏，2026-10-04用户随后手动确认本次保存保留；这是人工实机验证，探针没有读取服务端保存响应。

验证后手动关闭探针，并确认探针窗口退出，保留用户游戏。结论为真实双窗口后台菜单输入隔离、进度及最终保存行识别、单窗口连续只读开始→运行→视觉完成可用；官方脚本由用户手动运行，外层尚无自动闭环。真实FramePool、真实并发负载、多比例/DPI、锁屏、存档选择及其他场景保存持久化仍待验证，下一步见 [开发计划](backlog.md#脚本功能多账号日常)。

## 自动会话生命周期验证

LauncherModule.StartAutomationSessionAsync与面板启动共用互斥和并发限额，已有会话返回null，不激活或接管。AutomationGameSession由应用层内部签发，只暴露会话ID、账号ID、完成任务和绑定关闭入口。关闭前核对原对象引用，释放锁后仍只操作原对象；同账号重启后的新窗口不会被旧凭证关闭。关闭返回前释放原账号，完成回调与显式关闭共用释放方法，仅通知一次。现有面板重复启动仍会激活并报告已运行。

```powershell
dotnet test tests/BqtjLauncher.Application.Tests/BqtjLauncher.Application.Tests.csproj --no-restore --nologo -p:NuGetAudit=false --filter FullyQualifiedName~LauncherModuleTests
dotnet test tests/BqtjLauncher.Application.Tests/BqtjLauncher.Application.Tests.csproj --no-restore --nologo -p:NuGetAudit=false
```

新增6项虚构会话回归：跳过手动窗口、重复并发启动、仅关闭本次会话且可立即启动下一存档、重启后旧凭证失效、取消启动与取消关闭。相关12项及应用层全部97项通过，包含现有退出/重启检查；相关代码格式检查通过。已有NU1900为NuGet审计联网失败，没有新增代码警告。未构建产品开发包、未操作真实游戏；入口尚未接入面板或视觉工作进程，凭证只限制关闭对象，不代替业务完成判定。

## 自动容器通信与外层执行验证

2026-10-05新增专用后台启动模式：FlashGameRuntime为自动会话传入GUID，容器只在该模式创建本机当前用户专属管道，并取消加载完成后的Activate。管道客户端验证服务端容器PID，服务端只接受面板PID，双方核对会话ID；4KiB长度前缀JSON拒绝非法长度。目标仅扫描自身承载面板后代并核对自身原生子进程PID，空父句柄会被拒绝，不扫描桌面；x86句柄零扩展为64位整数传给视觉组件。目标就绪不代表已经登录或进入存档，工作进程仍须逐帧验证归属、尺寸与可见性。倍率设置只影响当前宿主，不写用户偏好；服务关闭取消等待和连接，不同步等待WPF调度器。

DailyScriptRunner已接新建/跳过→绑定→原速菜单→执行器观测→本次开始后应用方案倍率→面板侧稳定保存确认→绑定关闭。消息同时携带会话与运行GUID，旧截图、错身份、无开始证据、未完成的流结束不得关闭。取消、超时、游戏意外退出或校验失败保留窗口，独立8秒收尾恢复原倍率；倍率恢复或工作组件清理失败显式报告。阶段错误只使用固定说明，不输出异常原文或OCR文字。外层最多60分钟，目标读取最多60秒，只重试无输入的目标查询。

前期新增7项管道/协议测试、4项后台启动参数测试、9项外层生命周期测试，应用层117项曾全部通过。覆盖旧完成/错运行拒绝、红叉后正常保存、取消保留及倍率恢复、帧与退出竞态、恢复失败报告、1—8存档边界；均使用虚构目标/进度，不创建或关闭真实游戏。真实管道、后台启动焦点、存档与菜单自动识别和自动关闭仍待实机验收。本轮增量验证及开发包见下文。

2026-10-05界面检查：用户指定第三行左侧存档，点击后进入地图。存档页面为两列四行，不显示槽位编号；按行排列对应5，游戏内部编号尚未验证。放大窗口辨认后确认助理是游戏上方机器人图标，左栏有唯一“日常操作复制”，选中后下方出现三角“运行”，点击弹出“是否开始运行 日常操作复制？”及确定/取消。取消确认，保留手动窗口在脚本选择页；没有实际启动脚本或关闭窗口，没有保存真实截图、存档名称或原始OCR文字。这是人工界面检查，不代表视觉执行器已实现，也不把屏幕坐标当作可跨尺寸复用的识别结果。

当前已实现MaaDailyScriptExecutor和DailyWorker，并接面板“日常脚本”表单。x86面板通过标准输入传绑定身份，独立x64组件完成限定区域OCR、八卡片边框布局校验、定向输入及连续进度/绿色保存勾观测。首版仅支持950×600原生画面；菜单文字每阶段最多120秒；卡片同帧校验最多20秒，只读重采样并要求连续两帧一致，输入不重试。每轮检查目标归属、客户区、可见性及输入桌面；最小化、锁屏或失配结束外层并保留游戏，不自动恢复。取消经本次运行专属事件先协作停止；释放只核对原目标归属，停止或最小化不得阻止已按下消息释放，3秒未退出才终止自身组件，正常收尾也有上限。组件不记录原始OCR或游戏截图，Maa日志关闭。

本轮23项相关回归通过：5项虚构卡片布局、2项不显示控件的按下后取消/释放、9项外层生命周期、7项管道；不是全部测试重跑。开发面板与表单用Computer Use打开检查，控件正常显示；真实完整游戏流程仍待验收。最新DirectChild合成桌面回归在读取光标时失败，尚未发输入，不认定焦点测试通过；不显示控件的释放回归不依赖桌面，已通过。桌面构建仅既有NU1900，最终组件构建零警告。

日常开发包构建（先准备固定SDK和模型；不下载新版本、不进入发行链）：

```powershell
./tools/Build-DailyScriptPreview.ps1 -CompilerPath <32位gcc路径>
```

最新产物为`artifacts/dev/slot-b/BqtjLauncher.Desktop.exe`，版本`0.0.0-dev.20261005191420+c777edb20458d27671d110042ba9630c640d2d61`，源码c777edb加未提交改动；面板构建完成于2026-10-06 03:14（北京时间）。依赖共用`artifacts/dev/automation`，工作进程需要本机.NET 10 x64运行库；不能仅复制slot-b后宣称独立可分发。固定官方SDK逐文件和模型哈希校验通过。默认PATH中64位gcc曾被PE检查拒绝，指定32位编译器后成功；勿将本机路径写成工具默认值。

验收：先退出旧开发面板及其游戏（跳过已有会话仅覆盖当前面板），打开slot-b，选择账号→日常脚本→存档与倍率→启动日常。第三行左侧对应表单存档5。账号需已保存有效登录信息；验证码由用户处理，无法进入存档会超时保留窗口。先用100%、原速观察首页读取存档、选档、助理、脚本选择、运行确认、进度增长及最终保存后关闭；再验收方案倍率、停止、意外退出、遮挡不抢焦点、最小化/锁屏、100%/150%比例。首次UI识别参数尚未实机验证；失败记录阶段后修正，不重跑运行点击或降低保存门槛。用户手动验收前，不称为正式可用完整闭环。

2026-10-06启动反馈及回归：DPI、首页读取入口、弹窗标题和卡片等待已修正，17项相关回归曾通过；确认提示因OCR遗漏末尾问号持续等待，DailyRunPrompt改为完整具名提示允许标点遗漏，11项先失败后修复通过。用户已自动进入存档、确认并完成官方脚本。观察卡点在真实完成画面复现：旧区域(730,120,220,480)标题候选1但进度未解析，校准区域(759,154,182,420)标题候选1且进度22/22，两者保存均True，两次同帧对比一致。DailyProgressReader现在供工作进程与探针共用校准区域；修正后实机同帧旧区域仍失败、共用识别22/22及保存True。未持久保存图片或原始OCR，未发送游戏输入，也未把旧完成画面当作本次开始。领域42项、外层生命周期10项通过；构建仅既有NU1900，SDK/模型校验通过。已停止旧任务并退出探针，工作进程退出、游戏保留；停止后界面显示通用未完成文案，取消反馈待核对。新包从头自动进度增长及稳定保存后关闭仍待验收；其他比例、真实并发和最小化/锁屏未验收。

观察阶段只读回归：运行共用组件`artifacts/dev/automation/background-probe/MaaBackgroundProbe.exe`，确认目标已进入游戏，点击“只读检查日常进度”。它在同一帧比较旧区域与执行器当前共用识别，仅输出候选数、进度和保存布尔值，不运行官方脚本或关闭窗口。本次为避免覆盖运行中的组件，先在`artifacts/dev/automation/progress-probe`构建诊断副本；副本已退出，不是交付入口。完整闭环仍需从新建会话观察实际进度增长，单帧完成识别不代表自动关闭通过。

真实入口启动回归（仅本次创建的子进程、虚构目标，不枚举或操作游戏）：

```powershell
./tools/Test-DailyWorkerStartup.ps1
```

此命令旧包报告DPI=0失败，新组件报告PerMonitor=2及虚构目标TargetChanged=2通过。它验证DPI启动模式及结构化拒绝，不证明真实登录后的目标稳定或完整游戏流程。

## 用户反馈与诊断工具


面板右上角提供“打开日志目录”和“导出诊断包”。用户选择ZIP保存位置后，后台导出启动器版本、系统/运行库/架构信息及最近最多7份日志摘要；每份只取末尾2MiB。没有日志时仍可导出环境信息，文件不可读会在环境信息中记录跳过数量。

摘要只保留时间、级别、固定事件类别、错误类型和有限数值，不原样复制异常正文、URL、账号信息或完整日志；不读取账号数据库或Cookie，不自动上传。用户反馈应附版本、操作步骤、发生时间和截图，必要时自行提交诊断包。不要上传整个用户数据目录或launcher.db。

原始日志位于 `%LOCALAPPDATA%\BqtjLauncher\logs`，按天或达到5MiB滚动，最多保留7个文件。面板与容器显式启用共享文件写入。白名单摘要为降低信息泄露而舍弃部分细节，不能视为完整现场转储。

开发侧还有 Measure-GameMemory.ps1（仅仓库内进程资源采样）与 Get-GameWaitChain.ps1（指定进程线程等待关系），未随正式包分发。测试仅使用临时虚构日志；agent不得打开或导出用户的真实日志作为测试材料。
