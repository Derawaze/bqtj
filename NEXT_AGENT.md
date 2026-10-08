# 当前交接

更新：2026-10-08。任务优先级只在 [backlog](docs/backlog.md) 维护。

## 当前基线

- 最新正式版为 [v0.1.5](https://github.com/Derawaze/bqtj/releases/tag/v0.1.5)，于北京时间 2026-10-08 19:45:34 公开，已标记为正式版。标签固定在 938250a；与已验收 dev/5676c9f 相比，产品仅更新正式版本信息，后续 main 文档提交不改变标签或程序。
- 用户于 2026-10-08 验收开发包 0.0.0-dev.20261008105040 通过，授权 main 推送、正式构建及公开 GitHub Release。Release 说明按要求只保留修复内容；后续版本仍需重新验收和授权。
- 原生迁移暂停于 codex/native-lightweight/69434dd，不接入生产链；Maa 仅在 codex/maa-development。本地 dev/075c9c7 含验收与清理文档，origin/dev 仍为已验收产品 5676c9f；后续修复开始时同步当前正式基线。

## 当前交付与验证

- 公开 ZIP：`BqtjLauncher-v0.1.5-win-x86.zip`，61,680,496 字节（58.82MiB）；原 WPF/.NET 10 自包含版本，解压即运行。对应 SHA256 文件已公开且与 GitHub 附件摘要一致：`14b19d4e0073e018ccc32d11352cff28aa28d093368bea344b48adf08b94ae9a`。
- [正式 Release 工作流](https://github.com/Derawaze/bqtj/actions/runs/37771871719) 已成功，恢复/117 项测试、格式检查、原生命令恢复、x86 正式构建与 ZIP/SHA 验证均完成；先完整上传草稿后公开，ZIP 与校验文件均为 uploaded。Release 工作流现为启用状态。
- main/e919d41 的 [CI](https://github.com/Derawaze/bqtj/actions/runs/37770238934) 已成功。标签仅新增修复说明与公开授权文档，不改变已验收产品代码。
- 内容包括加载中缩放、全屏首次显示、原生宿主大地址支持、旧实例释放和命令超时恢复、取消固定多开上限、移除容器“账号密码”按钮；面板账号管理与自动登录保留，详见 [修复说明](docs/release-v0.1.5.md)。
- 本地先行正式包仍为 `artifacts/release/BqtjLauncher-v0.1.5-win-x86.zip`，来源 5b97ec7，构建于北京时间 19:24:41 至 19:24:59，SHA256 为 `ef3e8a8d1f7822fd52b19805b6d9025d0e7d04d65bc630d2eb200e41f435c844`。公开附件由标签工作流重新构建，源码修订元数据及校验值不同，不覆盖原本地交付证据。
- 本地正式包五文件、两入口 x86、大地址标志与 SHA 通过；从该目录启动宿主的三会话合成 Cookie、刷新/重建浏览器与重启通过。没有读取真实账号、Cookie、数据库或生产日志。公开附件临时下载目录已清理，不留重复包。
- 已验收开发包 20261008105040、回退包 20261008094647、正式 v0.1.4 与必要诊断证据保留；此前清理 16 项旧产物与 6 份失效验收文档，释放 122.49MiB。

## 尚未解决

- 长时卡死与地图透明仍未稳定复现；用户反馈刷新可恢复，不据此认定内部内存泄漏已解决。证据见 [加载诊断](docs/diagnostics/flash-next-level-stall.md)。跨屏 DPI 与真实多开长时承载未单独验证。
- 加载缩放实际像素、20 次刷新与高位地址旧证据保留，见 [显示诊断](docs/diagnostics/viewport-loading.md)；本轮没有将旧证据冒充重新实玩。

构建、测试、发布串行，见 [开发手册](docs/development.md) 与 [构建规范](docs/build-policy.md)。
