# 当前交接

更新：2026-09-29。任务优先级只在 [backlog](docs/backlog.md) 维护。

## 当前基线

- v0.1.4 已正式发布，源码提交 eb7cdbb，99项本地测试及 GitHub CI/Release 均通过；内容见 [发行说明](docs/release-v0.1.4.md)。远程最新正式版为 v0.1.4，本地历史编号已废除，版本序列仅以远程正式发布为准。
- 当前功能：多账号独立会话、明文凭据及单次自动登录、刷新/静音/变速、比例适配、GitHub更新检查。更新只打开对应发布页，用户自行下载。
- 退出清理链使用 ConfigureAwait(false)，已得到失败→通过回归；正常退出、超时终止及整层释放检查通过。证据见 [诊断](docs/diagnostics/shutdown-and-text-check.md)。
- F3已改用窗口级 RegisterHotKey，用户此前验收通过；原生消息钩子方案已否证。启动阶段分段超时及重试已纳入当前版本。
- 文字审核卡住用户暂未复现，等待新现场；没有根因或修复结论。

## 构建与接手

遵守 [构建规范](docs/build-policy.md)。新迭代只写 artifacts/dev；本次授权仅限 v0.1.4，后续仍须用户手动验收并明确下令。正式产物在 artifacts/release，托管结果以对应标签工作流为准。

旧开发包和正式包已按用户指令删除，释放约801 MiB；旧 slot-a/slot-b 链接已失效。构建、测试、发布串行，避免 bin/obj 冲突。先处理用户新任务，不主动展开候选。

## 容易回归的地方

- 游戏入口必须每次解析平台当前版本，失败回退缓存/随包地址，不能固定旧包装页。
- 登录管道用 ReadFile；禁止恢复阻塞 fgets + fflush(NULL)。相对 action 与 value 伪占位符必须兼容。
- 原生启动先 stage 后 ready，读超时后必须重启，不能复用 stdout。
- 显示回执检查子窗口 WS_VISIBLE；Flash NoScale/左上对齐需校准为 ShowAll/居中。
- Flash内存不受.NET GC管理；未证明游戏内存泄漏已解决。
