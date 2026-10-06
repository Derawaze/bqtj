# Flash 长时加载异常

更新：2026-10-07。

## 当前证据

用户报告 v0.1.4 长时游玩后卡在加载界面，或地图/场景透明；补充“刷新游戏可以恢复”。刷新只重建浏览器与 Flash、不退出宿主，支持旧实例资源或加载状态累积这一方向，但尚无真实症状的稳定复现，也没有确认 Flash 内部内存泄漏。2026-10-07 用户对发生时是否变速回答“记不清或两种都有”，因此没有证据将变速列为主要原因，也不能据此确认 1× 已独立复现。

仓库没有 1GB 内存限制、Job Object 内存限制或工作集上限。基线 x86 宿主未启用 IMAGE_FILE_LARGE_ADDRESS_AWARE；仅保留、不提交物理内存的高位地址探针，在 0x90000000 分配失败，启用后通过。x64 Windows 的 x86 进程可据此使用最多 4GB 虚拟地址空间，具体依据见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/dxtecharts/sixty-four-bit-programming-for-game-developers)。

## 本轮改动与验证

- 原生宿主构建开启大地址支持，并在构建后检查 PE 标志；保持 x86，不更改或分发 Flash。
- 刷新及退出共用显式释放路径：解绑浏览器事件、Stop 旧导航、关闭 OLE 实例、释放 COM 引用并销毁控件窗口。进程内 Cookie 配置仍只在进程首次启动时执行。
- 自制离线 SWF 的 20 次生产刷新循环通过：旧 Flash HWND 已销毁，新实例均能加载并正确放大；一次记录中私有提交量约 51.7→53.8MB，句柄 777→778。有限循环不能外推到真实地图切换或长时在线。
- 交付目录的三账号本地 HTTP 合成 Cookie 探针通过并行、浏览器刷新及进程重启；未读真实账号或 Cookie。旧探针把 ready 前的 stage 启动进度误判为错误，已修正。沙箱内合成 Cookie 写入报 12004，沙箱外通过。

增加地址空间是资源加载余量改善，不等于释放游戏内部对象，也不能消除地址碎片或网络错误。真实长时症状保持待验收。

## 4399Start 对照分析

2026-10-06 只读分析用户提供的 4399Start 安装目录。目录只有程序与用户数据，没有源码；未运行该启动器或清理命令，未读取 `data.json`、`recovery.json`、真实 Cookie 或日志。使用本机 UPX 对程序副本解包，再检查 API 声明、字符串和调用指令；副本与探针只留在本仓库已忽略的 `artifacts/dev/4399-analysis/`，不随源码或发行包公开。

分析对象为原始 `data/4399Main.exe`，SHA256：`4F1193C5950E74E38BDC2B8B201246E1EEC5551AA8B7E281BBFD803FC49F1D40`。以下地址均为解包副本中的虚拟地址，可用 `main-disassembly.txt` 核对。

| 功能 | 已确认实现 | 对当前问题的意义 |
|---|---|---|
| 优化内存 | `0x423b10` 调用 `GetCurrentProcess()`，随后 `SetProcessWorkingSetSize(handle, -1, -1)` | 修剪驻留页，没有在此函数中释放 Flash 对象或卸载地图 |
| 清理缓存 | `0x40571a` 拼接并执行 `RunDll32.exe InetCpl.cpl,ClearMyTracksByProcess`，第一参数选择 `253` 或 `4351` | 调用 IE 系统清理入口，未传入游戏域名或资源 URL，无法限定为单个游戏缓存 |
| 大地址支持 | 原始游戏宿主为 x86，PE Characteristics 为 `0x012f`，含 `0x20` | 与本轮已补上的大地址支持一致，扩大地址空间余量 |

内存路径的 API 声明索引为 `0x34`（GetCurrentProcess）和 `0x35`（SetProcessWorkingSetSize）；`0x423b48`、`0x423b4d` 连续压入两个 `0xffffffff`，`0x423b5a` 调用 DLL 分派器。直接调用入口包括菜单命令 `0x4e31`（`0x422b11`）、消息分派参数 `0x28`（`0x4234d8`）以及显示“优化内存完成”的处理函数（`0x435363`）。有多个入口不等于已证实定时自动优化；未运行程序和读取其设置，触发频率保持未验证。

缓存函数中的第一参数为零时选择 `253`，非零时选择 `4351`；“清理缓存”反馈路径 `0x412a8f` 和 `0x435010` 均传零，选择 `253`，另有 `0x43aac4` 传一的分支。未找到这些组合值的 Microsoft 正式参数说明，也未执行清理验证，因此不把逐项清理范围或 Cookie 是否保留写为已确认事实。WinINet 的磁盘缓存由同一 Windows 用户下的应用共享，依据见 [Microsoft 缓存说明](https://learn.microsoft.com/en-us/windows/win32/wininet/caching)；系统级清理不适合直接接入当前账号隔离链。

原始程序的 PE 标志作为运行行为依据。UPX 解包副本恢复为 `0x010f`，不含大地址标志；不能拿副本的标志误判原始程序，也没有运行该副本。

### 合成数据验证

`artifacts/dev/4399-analysis/WorkingSetTrimProbe.c` 只在自己的 x86 进程分配 128MiB 虚构数据，每页写入一个字节，执行上述工作集修剪后重新读取原数据，最后实际释放。一次运行结果：

| 阶段 | 私有提交量（MiB） | 工作集（MiB） |
|---|---:|---:|
| 分配并触碰所有页 | 129.09 | 133.18 |
| 修剪工作集 | 129.10 | 0.23 |
| 重新触碰原数据 | 129.10 | 128.39 |
| VirtualFree 实际释放 | 0.85 | 0.41 |

原数据 32768 页校验通过。工作集下降表示物理驻留减少；对象、地址空间与私有提交量仍保留，访问后驻留重新增长，与 [SetProcessWorkingSetSize](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsize) 的说明一致。它可以暂时让出驻留内存，不能回收游戏仍持有的资源，也无法修复虚拟地址耗尽；频繁修剪还会增加随后访问的缺页处理。

### 可借鉴范围

本轮已补齐对照启动器具有的大地址支持，并加强刷新时旧实例释放。工作集修剪不作为长时卡死修复；缓存清理只在缓存异常有证据时考虑按资源 URL 的手动恢复，不能代替销毁旧 Flash 实例。当前不新增自动优化、全局清缓存或 GC 功能。

“该启动器不会卡死”仍是用户转述，静态调用链与合成探针不能验证其真实长时稳定性，也不能排除宿主、Flash 版本、变速设置、网络或游戏内容差异。后续判断仍需真实复现前后的私有提交量与刷新效果。

## 恢复链路的进一步证据

修复前控制器的 ReloadAsync、ShowAsync、变速和显示就绪查询均在 `ReadHostLineAsync` 外套 10 秒 `WaitAsync`；底层 `StreamReader.ReadLineAsync` 没有由该超时驱动的取消。超时退出会释放响应锁，但原读取可能仍在占用流。

`artifacts/dev/loading-analysis/CommandReadTimeoutProbe.ps1` 用本进程虚构异步管道复刻这一模式，100ms 内稳定得到：

```text
wait-timeout=True; underlying-read-pending=True; next-read-rejected=True;
late-reply-consumed-by-old-read=True; reproduction=PASS
```

这验证了读取机制的缺陷：第一次等回执超时后，下一次读取可能被拒绝，迟到的回执仍被旧任务消耗。没有运行真实 Flash 卡死场景，也没有证明这是长时地图透明的根因。语义依据见 [Task.WaitAsync](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0) 与 [StreamReader.ReadLineAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamreader.readlineasync?view=net-10.0)。

2026-10-07 已实现独立 NativeHostCommandChannel：宿主 ready 后 stdout 只有一个后台读取任务，命令与回执使用 `request <编号> <命令>` / `reply <编号> <结果>`。10 秒上限结束的是当前请求，旧回执继续被唯一读取者读走并按编号丢弃；半行回执也不会因请求超时丢失前半段。凭据与 resize 沿用单向协议，共用写锁；原生端保留零编号旧命令供既有探针使用。

关闭窗口时取消当前操作与排队等待，关闭 stdin 请求原生线程退出；挂起宿主仍沿用原有的有界进程退出路径。变速即使目标等于最后一次确认值也重新向宿主确认，避免旧超时命令迟到生效后误信缓存。此改动不重启暂时响应变慢的宿主，进程内登录态保留。

验证：10 项通道回归覆盖正常回执、不回复后再次刷新、相同内容的迟到回执、半行回执、取消、关闭与宿主断开；全量 .NET 109 项通过。`Test-NativeCommandRecovery.ps1` 连接生产模块与原生命令线程，用隐藏虚构窗口模拟主线程阻塞，实际匿名进程管道、编号速度错误回执、旧协议、控制器启动/刷新及关闭当前和排队请求全部通过。该夹具没有加载 Flash，速度错误是预期结果。原生像素缩放、20 次刷新、高位地址及 CRT 空闲管道仍通过；发布目录三账号 HTTP 合成 Cookie 探针验证了带编号及旧命令刷新。详细版本与校验值保存在开发包旁的验收说明。

真正的长时症状仍缺稳定复现。已有 Flash ReadyState 查询只反映初始控件就绪，不能识别 ActionScript 后续地图/场景加载；不能据此定时判定卡死并自动刷新。后续现场证据优先记录刷新前后的私有提交量、虚拟空间余量、CPU 与句柄数量，并对比 1× 与变速。若私有提交持续增长且刷新不回落，再验证完整进程重启的释放效果；内存平稳时改查加载与计时链，避免先把所有透明场景归因于内存。

## 恢复与后续观察

“刷新游戏”会中断当前关卡，但保留宿主进程内登录态；若容器无法响应，管理面板可重启所选账号容器，5 秒未退出才终止进程，再启动该账号。

内存观察命令见 [开发手册](../development.md)。使用 PID、私有提交量、工作集与句柄数记录复现时间；不读取账号库、真实 Cookie、日志或进程内存内容。Flash 内存不由 .NET GC 管理，不自动 GC、清理全局 IE 数据或修剪工作集；工作集下降不能作为泄漏修复证据。
