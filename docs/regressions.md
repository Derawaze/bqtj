# 回归要点

仅在修改对应功能时读取。操作命令统一见 [开发手册](development.md)，任务优先级统一见 [backlog](backlog.md)。历史现场全文可从 Git 历史查阅，不保留逐轮交接记录。

| 功能 | 已确认原因与必须保留的实现 | 验证入口与边界 |
|---|---|---|
| 登录冻结 | 阻塞 fgets(stdin) 持有 CRT 流锁，UI线程 fflush(NULL) 等待同一把锁；改用 ReadFile，超长行整行丢弃 | NativeCommandPipeProbe：空闲管道下旧代码超时、新代码通过；Responding=true 不能证明嵌入浏览器正常 |
| 自动登录 | 平台用户名提示是 value 伪占位符；登录 action 可为固定根相对路径。仅可信 HTTPS、同表单且可编辑的字段允许填充；保留用户已有输入，只点一次提交，不直接 form.submit | LoginAutofillProbe、CredentialPipeProbe；真实登录由用户验收，验证码交给用户 |
| 凭据传递 | 使用绑定父子进程的限长UTF-16十六进制管道，不经 URL、命令行或剪贴板；十六进制不是加密 | 虚构凭据覆盖换行、引号、冒号；不读取用户账号库或日志 |
| 运行时隔离 | IE创建前设置一次 INTERNET_SUPPRESS_COOKIE_PERSIST，失败停止；刷新不能重复设置，否则可能丢自己的登录态 | WinInetCookieProbe、NativeCookieSessionProbe；多开已验收，不外推 Flash LSO、DOMStore 完整隔离 |
| 150%画面缩在左上 | 浏览器光学倍率正确仍可能被Flash ScaleMode=3/NoScale、AlignMode=5覆盖；resize/scale/show校准为0/ShowAll、0/居中 | StartupZoomProbe旧代码失败、新代码通过；ViewportProbe四组尺寸通过。跨屏DPI、所有战斗点击未全面覆盖 |
| 显示与加载 | 根据物理客户区等比居中留黑边；就绪后固定1秒黑底。显示回执检查子窗口 WS_VISIBLE，不能用同时检查隐藏父窗口的 IsWindowVisible | 实机全屏、最大化还原、刷新已有证据；修改加载层时应观察连续加载过程 |
| F3变速 | 焦点落在独立IE/Flash子进程时WPF键盘事件收不到；原生线程消息钩子方案已实测否证。当前用 RegisterHotKey + HwndSource，保留WPF路径及300ms去重，关闭时注销 | 用户已验收游戏内切档；修改时检查点击游戏前后、按住、多窗口及其他应用中的行为，不将旧文档的前台隔离推断当作保证 |
| 宿主启动 | 先stage再ready，分阶段等待；读超时后stdout不可复用，必须重启宿主 | NativeHostStartupDetailTests、NativeHostStartupCommandsTests；不要恢复单一固定超时 |
| 退出残留 | OnExit同步等待释放，异步关闭链若捕获WPF上下文会互相等待；退出等待、CloseAllAsync、DisposeAsync使用ConfigureAwait(false) | GameSessionShutdownTests旧代码失败；正常退出、5秒超时终止和LauncherModule整层释放回归通过。不代表所有残留原因都已排除 |
| 日常进度观察不结束 | 工作进程使用扩大侧栏区域，实机同帧无法解析进度；校准区域能识别22/22。DailyProgressReader共用校准区域及保存识别，保留当前运行增长和稳定保存门槛 | 探针只读检查日常进度：旧区域未解析、共用识别22/22及保存True；领域与外层生命周期测试。单帧识别不替代完整自动关闭验收 |

## 内存与恢复

尚无证据证明存在固定1GB限制或已修复Flash泄漏。重启所选账号可回收其进程资源，但会中断关卡；这只是恢复方式，不是随机卡关的根因修复。不得用清Cookie、强制.NET GC或修剪工作集冒充修复。
