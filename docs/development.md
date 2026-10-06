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
## 用户反馈与诊断工具

面板右上角提供“打开日志目录”和“导出诊断包”。用户选择ZIP保存位置后，后台导出启动器版本、系统/运行库/架构信息及最近最多7份日志摘要；每份只取末尾2MiB。没有日志时仍可导出环境信息，文件不可读会在环境信息中记录跳过数量。

摘要只保留时间、级别、固定事件类别、错误类型和有限数值，不原样复制异常正文、URL、账号信息或完整日志；不读取账号数据库或Cookie，不自动上传。用户反馈应附版本、操作步骤、发生时间和截图，必要时自行提交诊断包。不要上传整个用户数据目录或launcher.db。

原始日志位于 `%LOCALAPPDATA%\BqtjLauncher\logs`，按天或达到5MiB滚动，最多保留7个文件。面板与容器显式启用共享文件写入。白名单摘要为降低信息泄露而舍弃部分细节，不能视为完整现场转储。

开发侧还有 Measure-GameMemory.ps1（仅仓库内进程资源采样）与 Get-GameWaitChain.ps1（指定进程线程等待关系），未随正式包分发。测试仅使用临时虚构日志；agent不得打开或导出用户的真实日志作为测试材料。