# 当前技术决策

只记录仍有效的选择与不能丢失的原因；模块位置见 [实现地图](architecture.md)。

| 选择 | 原因与边界 |
|---|---|
| WPF / win-x86 / 本机 Flash ActiveX | 游戏依赖32位OCX，WebView2不能承载ActiveX；不分发Flash或游戏资源 |
| 每账号独立WPF容器 + 原生Flash子进程 | 同进程多个ActiveX曾使面板崩溃；原生故障限制在该账号 |
| 原生宿主内连续虚拟时钟 | 旧变速DLL进入.NET时触发CoreCLR崩溃，高倍率切换也曾停止刷新；不加载旧DLL |
| 仅挂钩Flash OCX的计时导入 | 锁内先按旧倍率结算，再改变时间斜率，避免时间倒退和跳变；不改系统DLL |
| 进程内Cookie会话 | 满足运行时多开，退出后重新登录；保存的账号密码可用于自动登录 |
| 不采用AppContainer | 合成Cookie隔离通过，但实际音频返回E_ACCESSDENIED，IE/WinINet导航失败 |
| 不采用新增系统用户或Sandboxie | 用户拒绝新增Windows用户，当前不推进持久隔离，也不引入服务/驱动依赖 |
| SQLite明文保存凭据 | 用户明确选择；凭据只经绑定的父子管道传递，不放命令行、URL或日志 |
| 压缩自包含发布 | 保持四文件交付且不要求另装.NET；不手动删除运行依赖，不启用WPF不兼容裁剪 |

## Cookie边界与证据

原生宿主在OleInitialize和浏览器创建前，一次性设置INTERNET_OPTION_SUPPRESS_BEHAVIOR的INTERNET_SUPPRESS_COOKIE_PERSIST。失败停止启动，刷新不重复设置；重复设置曾导致自己的会话丢失。原磁盘Cookie不清空。

合成WinINet A/B测试、三个真实IE宿主并行、浏览器重建刷新、退出后重新启动均通过；旧rc.6作为负向对照会串号。用户已验收真实多账号并行。Flash LSO、DOMStore等其他存储未获完整隔离保证；长时在线、跨关卡不由上述测试推定通过。

相关诊断源码：native/Diagnostics/WinInetCookieProbe、NativeCookieSessionProbe。使用虚构Cookie，不枚举真实站点数据。

```powershell
dotnet build native/Diagnostics/WinInetCookieProbe -c Release
./native/Diagnostics/WinInetCookieProbe/bin/Release/net10.0-windows/WinInetCookieProbe.exe matrix session
./tools/Build-NativeFlashHost.ps1 -OutputDirectory ./artifacts/native-session
dotnet build native/Diagnostics/NativeCookieSessionProbe -c Release
./native/Diagnostics/NativeCookieSessionProbe/bin/Release/net10.0-windows/NativeCookieSessionProbe.exe ./artifacts/native-session/BqtjNativeFlashHost.exe
```

## 游戏入口与恢复

运行时从已确认的sbai包装页加载对应版本SWF；sda直接资源曾返回访问错误。实际地址以代码配置为准，不在文档维护第二份版本地址。

容器卡住时由面板重启该账号：先请求正常退出，5秒后仍未退出则终止，再启动替代容器。此操作中断当前关卡；它是恢复手段，不能证明所有卡顿根因已修复。不自动清理全局IE数据或修剪工作集。

