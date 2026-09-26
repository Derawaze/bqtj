# 变速快捷键与焦点

## 现象与根因

进入游戏后按 F3 没有反应；有时先点击一下游戏画面再按才生效。

原因是按键归属：容器窗口是 WPF 进程，游戏画面是**独立原生宿主进程**里的 `Shell.Explorer.2` / Flash 子窗口。焦点在 WPF 工具栏时按键走 WPF 的 `PreviewKeyDown`；一旦点击游戏画面，键盘焦点转移到原生子窗口，WPF 收不到按键。

曾尝试在原生宿主里装线程钩子（先 `WH_GETMESSAGE`、后 `WH_KEYBOARD`）截获 F3 并经管道上报，**实测不可靠**：在容器里逐个窗口注入按键后，只有按键进入宿主的 WPF 表面窗口时才触发钩子；投给宿主窗口、`AtlAxWin`、`Internet Explorer_Server`、`MacromediaFlashPlayerActiveX` 时钩子都收不到，真实按键被 IE/Flash 直接取走。该方案已撤销，不要再用消息钩子做快捷键。

## 当前实现

- 容器窗口用 `RegisterHotKey(handle, id, MOD_NOREPEAT, VK_F3)` 注册**窗口级热键**（`FlashHostWindow.RegisterRecentSpeedHotKey`）。系统在该窗口处于前台时投递 `WM_HOTKEY`，与键盘焦点落在哪个子窗口无关，因此游戏画面或 Flash 控件持有焦点时同样有效。
- `WM_HOTKEY` 由 `HwndSource` 钩子接收并转到 `OnF3HotKey`；`WM_HOTKEY` 不是普通按键，子窗口收不到它，不会与游戏冲突。
- 焦点在 WPF 窗口时原来的 `PreviewKeyDown` 仍保留，两条路径共用 `OnF3HotKey`。
- 按住 F3 时 `WM_HOTKEY` 会连续投递（`RegisterHotKey` 没有 `MOD_NOREPEAT` 效果），因此用 300ms 保护窗口去重。
- 热键注册失败（例如 F3 被其它程序占用）只记录日志并退回 WPF 路径，不阻止游戏启动；窗口关闭时 `UnregisterHotKey`，避免同一账号再次启动容器注册失败。

## 修改这一块时的注意点

- 不要再引入宿主侧消息钩子或管道事件来送快捷键；上述实测已否决，会让 F3 静默失效。
- 热键是系统级资源，必须成对注册/释放，否则同一账号第二次启动容器会注册失败。
- `WM_HOTKEY` 会持续重复投递，去重保护窗口不能删掉。
- 侧键与游戏按键冲突：热键只在容器窗口处于前台时生效，不会在其它程序里抢 F3。

## 回归验证

- 容器内 F3 端到端：启动真实容器，向容器窗口投递 `WM_HOTKEY`（id 与生产一致），读 UI 自动化中“变速”按钮提示，档位应在当前档与上一档之间互换。开发期可用 `artifacts/ContainerF3Harness` 复现。
- 日志证据（`%LOCALAPPDATA%\BqtjLauncher\logs`）应出现：`F3 热键已注册（窗口级，不依赖焦点）`、`收到 F3：... previous=<上一档>`、`F3 互换：<当前> → <上一档>`。
- 实机验收需在游戏中分别验证：不点击画面按 F3、点击画面后按 F3、按住 F3、切换窗口后按 F3。
