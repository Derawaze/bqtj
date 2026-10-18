# 爆枪突击启动器 覆盖更新包 v{{VERSION}}

本包用于覆盖已解压的完整启动器；首次使用请下载完整发行包。

1. 关闭所有启动器面板、账号管理工具和游戏窗口，确认对应进程已经退出。
2. 将三个EXE及包内scripts目录合并复制到原目录，替换包内同名文件；保留第三方声明、scripts/plans用户方案和自己添加的脚本。不要删除整个scripts目录。
3. 重新运行 BqtjLauncher.Desktop.exe。

双击 BqtjAccountTool.exe 打开账号管理工具；也可从面板“管理账号”和新增/编辑弹窗打开，已有窗口直接唤起。左侧支持搜索、明文查看、原位编辑与多选删除，右侧批量导入导出。TXT 每行按“名称 账号 密码”填写，三项用空格分隔；导出文件含明文密码，请妥善保存。

账号和明文凭据保存在 `%LOCALAPPDATA%\BqtjLauncher\launcher.db`，偏好也在同一目录；覆盖更新时保留该目录。本包仍需已注册的32位Flash ActiveX。

具体改动与已知限制请查看 [对应版本说明](https://github.com/Derawaze/bqtj/releases/tag/v{{VERSION}})。开发覆盖包尚未正式发布时，以本次开发交付说明为准；不代表用户已经验收。
