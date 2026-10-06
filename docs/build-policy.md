# 构建与版本规范（agent 必须遵守）

## 分支与开发阶段

- `main`维护正式版；`codex/maa-development`保存尚未合入的Maa脚本工作。普通新改动使用`codex/`分支，不能因脚本勉强可用就合入main。
- 所有可交付开发产物只写入仓库`artifacts/dev/`；日常预览固定`slot-a` / `slot-b`，原生中间产物也放在该目录。`src/tests`下的`bin/obj`是编译缓存。
- 默认使用`Start-DevLauncher.ps1 -BuildOnly`或开发监视器。开发ZIP使用`Publish-Release.ps1 -Development -Version 0.0.0-dev.<UTC时间戳>`，时间戳为`yyyyMMddHHmmss`。
- 开发分支/PR的CI只构建dev包，不递增正式版本、不创建正式Release。反馈注明目录、源码提交及未提交改动、构建时间和验证范围。

## main自动正式发布

2026-10-06用户明确授权：推送到main后，自动递增PATCH并发布GitHub Release。此规则替代每次main发布单独分配版本的旧流程；不授权agent自行提交或推送其他改动。

1. 产品改动先提供开发包，用户手动验收；用户明确下令合入或推送main后，main推送作为正式发布入口。流程配置/文档修改按用户授权执行，无需重建产品验收。
2. main的Release工作流通过测试、格式和打包检查后，读取远程已公开且非预发布的严格版本Release，按数值取最高版本并递增PATCH。无正式基线、查询失败或标签冲突时停止，不猜版本、不跳号。
3. 所有正式发布共用并发锁，只发布仍为main头的触发提交，标签绑定完整提交SHA。同一提交成功后的重跑不递增；失败只允许补全属于同一提交的草稿。
4. ZIP和SHA-256先完整上传草稿，再公开并标记Latest；自动生成提交变更说明。不覆盖公开版本，不把工作流配置完成当作托管构建通过。
5. 较大或不兼容升级须由用户明确选择MINOR/MAJOR；仍保留显式正式标签入口，须有`docs/release-v版本号.md`。本地正式构建仍须本次明确授权并传`-ReleaseApproved`。
6. main保护规则禁止删除和强推，保留独立开发者直接推送。规则配置在`.github/main-ruleset.json`；该文件存在不等于远程规则已经启用。

## 版本与目录

- 正式版本为`MAJOR.MINOR.PATCH`，标签`vMAJOR.MINOR.PATCH`，包名`BqtjLauncher-vMAJOR.MINOR.PATCH-win-x86.zip`，只输出`artifacts/release/`。
- 正式序列仅以GitHub远程正式Release为准。本地历史编号不占用版本号；禁止覆盖或移动已存在标签。
- 开发包统一`0.0.0-dev.<时间戳>`，不占用正式序列。构建配置Release不等于正式发布授权。
- 已存在的同名包/目录不得覆盖。失败须先调查残留，不通过换版本掩盖问题；`-HotUpdate`仅表示包结构，不能绕过目录或授权边界。

## 清理

仅清理明确生成的编译缓存、过期开发槽位和诊断临时产物。保留当前验收包、正式交付包、源码；不读取或删除账号库、Cookie、日志、证书。删除前确认绝对路径在仓库内，跳过运行中产物、重解析点及疑似用户数据。不得使用`git clean`或`reset --hard`。
