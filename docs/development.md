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

回归源：LoginAutofillProbe.c、CredentialPipeProbe.c、ViewportProbe.c、StartupZoomProbe.c、AddressSpaceProbe.c、NativeCommandPipeProbe.c、NativeCookieSessionProbe。
具体命令见对应 [诊断索引](README.md#决策与诊断)。LiveLoginAutofillProbe使用虚构值，默认不提交网络登录。不要把启动成功代替具体行为验收。
原生宿主每次改动都要重编译；本机没有32位MinGW-w64时不得把“C#测试通过”当作原生已验证。

窗口与刷新离线验证：
```powershell
./tools/Test-NativeViewport.ps1 -CompilerPath <32位gcc路径>
```
该脚本串行验证居中布局、真实Flash像素缩放、倍率/导航重置恢复、20次刷新、高位地址与空闲管道。像素测试短暂显示自己的无焦点夹具，不读取账号或加载游戏资源；中间产物仅写入artifacts/dev/probes。
NativeCookieSessionProbe按stage/ready协议等待启动，测试本地HTTP与唯一虚构Cookie。若沙箱写入合成Cookie报12004，需在允许写入测试Cookie的环境运行，不能当作产品会话隔离失败或跳过断言后宣称通过。

命令恢复验证：`./tools/Test-NativeCommandRecovery.ps1 -CompilerPath <32位gcc路径>`。
该脚本连接生产托管通道与原生命令线程，用隐藏虚构窗口模拟主线程短时阻塞，验证超时、迟到回执、取消、旧协议与控制器关闭；实际匿名进程管道也必须结束。输出仅 artifacts/dev/probes/command-recovery，不创建账号或加载游戏。夹具未加载 Flash，速度错误回执是预期结果。NativeCookieSessionProbe首个账号使用带编号刷新，其余使用旧命令，均需保持合成 Cookie。

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

原图保存在Desktop/Assets/icon-source.jpg。清理仅处理仓库生成目录，默认保留全部开发验收包和发行包，跳过运行中产物、疑似用户数据和重解析点；不要改用全仓库删除命令。bin/obj清理后需重新restore。

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
