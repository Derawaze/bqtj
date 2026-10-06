# 当前交接

更新：2026-10-06。优先级只在 [backlog](docs/backlog.md) 维护。

- 当前工作区为`main`，跟踪`origin/main`，维护正式版的小bug。本次配置前远程正式基线为v0.1.4；实际最新版本以GitHub正式Release为准，不用本地编号推断。
- Maa脚本的计划提交c777edb及实现提交a46269c已保存并推送到`codex/maa-development`，未合入main。用户反馈勉强可用但仍有小bug，暂缓脚本迭代；完整限制与接手状态保留在该分支。
- main包含71ba60f的日志目录入口、脱敏诊断导出及共享日志写入，原104项测试曾通过。原v0.1.4源码为eb7cdbb，发行证据见 [发行记录](docs/release-v0.1.4.md)。
- 本次配置main推送自动PATCH及Release，开发分支/PR仍只生成dev包；同提交重跑、标签冲突、草稿恢复及旧main跳过均有离线回归。托管结果须实查，不宣称仅配置即成功。
- main保护配置已准备`.github/main-ruleset.json`：禁止强推及删除，允许直接推送。GitHub CLI此前401，远程保护启用尚待登录恢复后的实际核验。

## 下一步

完成并核验发布流程配置，随后根据用户提供的新bug复现步骤小步修复正式版。不把Maa代码带入main，不延续旧的未复现调查。修改相关链路时按需查 [回归要点](docs/regressions.md)。

构建前读 [构建规范](docs/build-policy.md)：本地开发仅artifacts/dev；产品改动先手工验收，用户明确要求推送main后自动发布。工作区与分支切换不关闭现有游戏；新建正式版开发预览时确认其来源分支。
