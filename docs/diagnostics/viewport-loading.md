# 全屏、最大化与加载遮挡（2026-09-23）

## 原因与修复

旧原生 WM_SIZE 只把浏览器窗口改为客户区大小，不更新光学缩放，也未将固定950×600游戏居中。`ViewportProbe.c` 直接调用生产 resize 消息复现：1600×900仍为100%且浏览器矩形铺满，失败；原尺寸通过。

现在所有 resize 共用 `update_browser_viewport`，根据物理客户区选择可容纳的最大整数百分比，游戏矩形向上取整并居中，父窗口保留黑边。向下量化缩放避免裁切；仅实际光学倍率变化时调用 ExecWB，避免重复缩放增加闪帧。旧 scale 管道仍兼容，但实际倍率以客户区为准；WPF 原窗口档位和还原尺寸不改。

探针1600×900、950×600、1000×1200、1920×1038全部通过。Computer Use 在 .6/.7 实际游戏启动菜单检查最大化、全屏及还原，画面等比、黑边对称。未验证跨显示器DPI、所有窗口档位及实际战斗输入。

## 加载

原固定6秒黑底改为隐藏整个 HwndHost并显示加载提示，等待浏览器/Flash ReadyState，最长12秒后兜底显示。用户随后要求尝试固定1秒黑底，.7 在就绪等待结束后渲染黑底并等待1秒，再显示游戏；刷新复用同一加载锁和关闭取消令牌。SWF自身的黑色加载画面不属于启动器遮挡。

.5 实机发现 show-error：IsWindowVisible同时检查隐藏的父HwndHost。修正为确认浏览器子窗口具有WS_VISIBLE后回执，WPF再揭开父窗口；.6起验证通过。.5不交付。

.7 冷启动观察到黑底过渡后自动进入游戏加载；最大化后还原正常，刷新也观察到黑底过渡并恢复游戏启动菜单。短时截图未捕获蓝闪，不代表消除，仍需用户连续观察。不能继续通过增加无界等待掩盖问题。

```powershell
gcc -DUNICODE -o artifacts/ViewportProbe.exe native/Diagnostics/ViewportProbe.c native/FlashHost/virtual_clock.c -lole32 -loleaut32 -lshell32 -lwininet -luuid -lgdi32
./artifacts/ViewportProbe.exe
```

使用x86 GCC；探针只创建自己的隐藏窗口，不操作用户窗口，不访问网络。

## 2026-09-25：150%仍为左上角100%的根因

现场原生客户区1425×899，浏览器光学倍率224（系统DPI150%），Flash窗口1419×896，但Flash属性ScaleMode=3/NoScale、AlignMode=5/左上。仅修改为ScaleMode=0/ShowAll、AlignMode=0后，保留窗口立即等比填充，不需刷新或重新登录。

update_browser_viewport在resize/scale/show时校准当前文档flashgame的这两个属性，只在值不正确时写入，不重复加载SWF。离线StartupZoomProbe创建真实Flash控件并设为3/5，旧代码失败，新代码通过；四组ViewportProbe通过。探针需与生产一致使用链接参数 -Wl,--disable-nxcompat,--disable-dynamicbase，并安装32位Flash。空白浏览器倍率正确不能替代Flash内容缩放检查。战斗点击、切关及跨屏仍未覆盖。
