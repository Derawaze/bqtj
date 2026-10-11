# 当前交接

更新：2026-10-11。任务优先级只在 [backlog](docs/backlog.md) 维护。

## 当前基线

- 最新正式版为 [v0.1.5](https://github.com/Derawaze/bqtj/releases/tag/v0.1.5)，于北京时间 2026-10-08 19:45:34 公开，标签固定在 938250a。用户已验收开发包 20261008105040 并授权该版发布；后续版本仍须重新验收和授权。
- main 继续维护文档与回归工具；本次按用户要求补充账号数据库位置、移除废弃工具及清理旧构建产物，不修改产品运行源码、不重建发行包。
- 原生迁移暂停于 codex/native-lightweight/69434dd，不接入生产链；Maa 仅在 codex/maa-development。本地 dev/075c9c7 含验收与清理文档，origin/dev 为已验收产品 5676c9f；后续产品修复开始时同步当前正式基线。

## 当前交付与验证

- 公开 ZIP：`BqtjLauncher-v0.1.5-win-x86.zip`，61,680,496 字节（58.82MiB），原 WPF/.NET 10 自包含版本。公开 SHA256 为 `14b19d4e0073e018ccc32d11352cff28aa28d093368bea344b48adf08b94ae9a`；[正式 Release 工作流](https://github.com/Derawaze/bqtj/actions/runs/37771871719) 成功，117 项测试、格式检查、原生命令恢复、x86 构建与 ZIP/SHA 验证通过。
- 内容包括加载中缩放、全屏首次显示、原生宿主大地址支持、旧实例释放和命令超时恢复、取消固定多开上限、移除容器“账号密码”按钮；面板账号管理与自动登录保留，见 [修复说明](docs/release-v0.1.5.md)。
- WPF 窗口夹具修正 da1e549 的 [完整 CI](https://github.com/Derawaze/bqtj/actions/runs/37778932148) 已通过。5 秒等待误判、集合隔离及旧逻辑负对照的结论统一保留于 [回归要点](docs/regressions.md)。
- 本地保留最新正式包 `artifacts/release/BqtjLauncher-v0.1.5-win-x86.zip`（来源 5b97ec7，SHA256 `ef3e8a8d1f7822fd52b19805b6d9025d0e7d04d65bc630d2eb200e41f435c844`）及已验收开发包 `artifacts/dev/BqtjLauncher-v0.0.0-dev.20261008105040-win-x86.zip`（来源 5676c9f）。公开附件由标签工作流重新构建，修订元数据与本地先行包不同，不能混用校验值。
- 2026-10-11 清理 23 项编译缓存及 19 项过期产物，释放 789.65MiB；旧开发包 20261008094647、旧正式包 v0.1.4、迁移原型二进制、重复工具下载与可重建探针输出已移除。含日志的 Maa 试验资料保留在忽略目录，未读取或删除。
- 已清理旧地址 UI 脚本、第三方变速 DLL 探针、AppContainer/Sandboxie 独立诊断入口及旧版发行文档。WinInetCookieProbe 保留 shared/session 合成矩阵，编译、会话隔离及共享负对照通过；WorkingSetTrimProbe 源码迁入 native/Diagnostics，C 语法与工作流语法检查通过。本地文档链接、项目引用和脚本语法检查通过。故障结论及验证入口仍保留，不读取真实账号、Cookie、数据库或日志。

## 尚未解决

- 长时卡死与地图透明仍未稳定复现；用户反馈刷新可恢复，不据此认定内部内存泄漏已解决。证据见 [加载诊断](docs/diagnostics/flash-next-level-stall.md)。跨屏 DPI 与真实多开长时承载未单独验证。
- 加载缩放实际像素、20 次刷新与高位地址证据保留于 [显示诊断](docs/diagnostics/viewport-loading.md) 及加载诊断；清理生成物不代表重新实玩。

构建、测试、发布串行，见 [开发手册](docs/development.md) 与 [构建规范](docs/build-policy.md)。
