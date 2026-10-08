# 增量任务清单

更新：2026-10-08。

## 当前：跟踪长时卡死与场景透明

- 最新正式版 v0.1.5 已经用户验收并公开，修复内容见 [发行说明](release-v0.1.5.md)，交付和验证证据见 [当前交接](../NEXT_AGENT.md)。
- 长时卡死/地图透明仍未稳定复现；用户反馈刷新可恢复，是否与变速相关记不清，不能认定内部内存泄漏已解决。后续按实际反馈记录运行时长、切关/刷新结果及无隐私的内存计数，边界见 [加载诊断](diagnostics/flash-next-level-stall.md)。
- 跨屏 DPI 与真实多开长时承载尚未单独验证。加载缩放回归仍须覆盖 SWF 在 ReadyState=4 后改写 Stage，不能用静态单帧替代。

原生迁移暂停于 codex/native-lightweight/69434dd，不接入生产链；Maa 留在 codex/maa-development。后续版本须重新获得用户验收和发布授权，不沿用 v0.1.5 授权。

构建门槛见 [构建规范](build-policy.md)，操作见 [开发手册](development.md)。
