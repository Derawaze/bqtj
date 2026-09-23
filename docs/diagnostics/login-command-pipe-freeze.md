# 登录卡住与命令管道流锁

当前状态：管道流锁问题已修复，用户已验收多个账号并行。此结论不外推所有长期运行场景。

2026-09-21：用户报告 session-preview.1 输入卡顿、点击登录后整个窗口无法激活，并确认 rc.6 同样出现。不能把该问题单独归因于 Cookie 隔离。

## 现场与可复现证据

- Computer Use 两次激活窗口超时；进程存活且 CPU 基本停止增长，`Process.Responding` 仍为 True，不能用它判定嵌入浏览器正常。
- 对用户保留的 rc.6 原生宿主用本机 x86 GDB 短暂附加后立即分离，只采集不带参数的函数栈。主线程在 `msvcrt!fflush → _lock → RtlEnterCriticalSection` 路径等待；另一个线程等待管道 ReadFile。没有采集网页、密码或完整内存转储。
- `NativeCommandPipeProbe.c` 直接包含生产宿主代码，启动同一个 `command_reader`，父进程保持 stdin 打开但不输入，再执行 `fflush(NULL)`。旧 `fgets(stdin)` 实现超时，新系统管道读取实现通过。该探针证明流锁阻塞模式，不替代真实登录复测。

修复后的真实原生 IE HTTP 合成 Cookie 回归也通过：三个并行宿主各自保持 Cookie，刷新与重启符合预期，`passed=true, errors=[]`。此项仍不代表真实平台认证成功。

## 修复

`read_command_line` 使用 `ReadFile(GetStdHandle(STD_INPUT_HANDLE))` 读取控制协议，避免阻塞期间持有 CRT 的 FILE 锁。保留 CRLF/LF 行协议；超长行整行丢弃，避免截断后执行命令。未更改 Cookie 模式、账号存储或登录页面。

## 可重复验证

在已配置 x86 MinGW 的 shell 中执行（编译器目录需位于 PATH）：

```powershell
gcc -DUNICODE -O0 -g -o artifacts/NativeCommandPipeProbe.exe native/Diagnostics/NativeCommandPipeProbe.c native/FlashHost/virtual_clock.c -lole32 -loleaut32 -luser32 -lgdi32 -lshell32 -lwininet -luuid
./tools/Test-NativeCommandPipe.ps1 -ProbePath artifacts/NativeCommandPipeProbe.exe
```

旧实现：`FAIL: CRT flush blocked by idle command pipe`，退出 1。
修复后：`PASS: CRT flush completed while command pipe remained idle`，退出 0。

测试与发布共用bin/obj，必须串行，避免缺失PDB等构建冲突。当前发行验证见 [首版记录](../release-v0.1.0.md)。
