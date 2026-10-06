# 独立开发工作方式

默认一个开发者与一个agent，小步增量；仅在用户明确要求时并行。

1. 只读 CONTEXT、NEXT_AGENT 和本文件，再按任务查源码；不要默认遍历全部文档。
2. 一次推进一个明确结果。普通改动不另写方案；缺陷先建立可重复的失败信号，再修复。
3. 保留必要中文注释，不覆盖用户改动，不读取真实凭据、Cookie、数据库或日志。
4. 按影响范围验证；文档只查链接与一致性。已通过的检查不无故重跑，实机验收与自动测试分开表述。
5. 完成后就地更新当前事实、任务状态及未验证项，不追加逐轮交接历史。构建前遵守 [构建规范](build-policy.md)，历史授权不延续。

| 信息 | 唯一维护位置 |
|---|---|
| 稳定术语与用户边界 | [CONTEXT](../CONTEXT.md) |
| 当前基线与接手 | [NEXT_AGENT](../NEXT_AGENT.md) |
| 优先级和待复现项 | [backlog](backlog.md) |
| 目录、版本号、发布门槛 | [build-policy](build-policy.md) |
| 操作命令和验证选择 | [development](development.md) |
| 源码定位与技术选择 | [architecture](architecture.md) |
| 必须保留的故障原因与回归入口 | [regressions](regressions.md) |
| 当前发行说明和交付证据 | 当前 release-v*.md（标签工作流读取） |

删除过时或重复文档时先保留不可重建的结论，再更新引用。历史发行和长篇排查过程留在Git历史与GitHub Release，不在当前文档中复制。
