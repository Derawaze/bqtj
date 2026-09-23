# v0.1.0 首版交付与精简记录

日期：2026-09-23。用户确认现有功能作为首版，未授权增加新功能或公开推送。

## 交付内容

发布目录只包含 `BqtjLauncher.Desktop.exe`、`BqtjNativeFlashHost.exe`、`README.md`、`THIRD_PARTY_NOTICES.md`。主程序内嵌压缩的.NET运行库和SQLite；原生游戏宿主继续独立，以保持账号进程隔离。没有手工删除运行依赖或启用WPF不兼容的裁剪。

| 指标 | account-preview.7 | v0.1.0 |
|---|---:|---:|
| 文件数 | 289 | 4 |
| 解压字节数 | 155,895,459 | 66,664,175 |
| ZIP字节数 | 66,610,959 | 61,638,612 |

ZIP SHA256：`535b8101eb9240132e9e7e3f97013ce8f1abe21dc45b47f05daf0802deffecd1`。

使用[微软压缩单文件发布方案](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)，仍自包含。压缩会增加启动解压工作，Windows将原生库释放到当前用户临时缓存；上述体积不包含这部分运行缓存。正式包面板实际打开正常，账号库读取正常。未修改用户账号数据。

## 图标与重建

用户提供的JPG原图完整保留在 `src/BqtjLauncher.Desktop/Assets/icon-source.jpg`。运行 `tools/New-LauncherIcon.ps1` 生成16至256像素的ICO以及界面PNG；原图等比缩放留白，不改绘图案。ICO作为EXE资源与窗口图标，PNG作为面板标识。

后续构建与清理命令统一见 [开发手册](development.md)，不覆盖本记录对应的已交付包。

清理工具只处理仓库内生成目录，保留指定发行包、运行中产物及疑似用户数据，拒绝重解析点；不修改本地账号数据库、系统运行库或.git。首轮清理32个目标，释放2,343,998,774字节。用户随后明确授权结束旧版本：按可执行文件路径限定结束旧rc.6/session-preview的15个进程，再清理2个目标、600,412,710字节。累计释放2,944,411,484字节（约2.74 GiB）。artifacts只保留首版发行目录、ZIP和SHA256，最终包复核通过。

## 验证范围

60项.NET测试通过；正式包构建无警告；两个入口x86 PE、4文件白名单、ZIP SHA256通过。正式包图标/面板/SQLite读取通过，精简包真实游戏启动等待用户反馈（Computer Use检测到用户输入后停止干扰）。精简前 .7 自动登录、最大化还原、刷新和全屏有实机记录。

保留有效诊断源码，当前技术决策已收敛到 [决策说明](decisions.md)；旧可再生成EXE、下载的公开网页缓存、bin/obj和旧ZIP已清理。没有创建提交、推送、公开Release或替项目选择开源许可证。

