# 修改启动器文字

当前窗口使用WPF，界面由XAML定义，交互和动态提示由C#提供。XAML会编译进EXE；修改源码后需要重新构建，不能直接编辑已发布EXE里的界面。

## 文字位置

| 要改的内容 | 文件 |
|---|---|
| 主面板标题、按钮、当前账号日常与其他方案、使用提示、项目公开说明 | [MainWindow.xaml](../src/BqtjLauncher.Desktop/MainWindow.xaml) |
| 主面板随账号/脚本状态变化的说明、取消或失败提示 | [MainWindow.xaml.cs](../src/BqtjLauncher.Desktop/MainWindow.xaml.cs) |
| 游戏启动、更新检查等动态状态 | [MainWindowViewModel.cs](../src/BqtjLauncher.Desktop/MainWindowViewModel.cs) |
| 脚本管理按钮、栏标题、窗口提醒 | [ScriptManagerWindow.xaml](../src/BqtjLauncher.Desktop/ScriptManagerWindow.xaml) |
| 脚本管理的校验、保存、运行提示 | [ScriptManagerWindow.xaml.cs](../src/BqtjLauncher.Desktop/ScriptManagerWindow.xaml.cs) |
| 首次选档窗口的说明、取消按钮 | [SaveSlotSelectionWindow.xaml](../src/BqtjLauncher.Desktop/SaveSlotSelectionWindow.xaml) |
| 账号工具标题、显示密码、有总统列、导入说明 | [AccountTransferWindow.xaml](../src/BqtjLauncher.Desktop/AccountTransferWindow.xaml) |
| 新增/编辑账号弹窗标签、总统开关及说明 | [AccountEditorWindow.xaml](../src/BqtjLauncher.Desktop/AccountEditorWindow.xaml) |
| 新安装时生成的官方模板及注释 | [BuiltInScriptPlans.cs](../src/BqtjLauncher.Application/BuiltInScriptPlans.cs) |
| 新账号自带的日常方案与注释 | [AccountDailyPlans.cs](../src/BqtjLauncher.Application/AccountDailyPlans.cs) |

已有安装的方案保存在主程序同目录scripts/plans/，可以直接在脚本编辑器修改；模板源码只决定新安装/初始化的内容，不会覆盖已编辑方案。

## 直接修改示例

用编辑器的全项目文本搜索找到要修改的原句，优先看上表对应文件。

    <Button Content="管理脚本" Click="ManageScripts_Click"/>
    <TextBlock Text="这里是说明文字" ToolTip="鼠标悬停的说明"/>

只改Content、Text、Title、ToolTip等展示内容。例如把Content="管理脚本"改为Content="脚本中心"，保存后重新构建。

- 不修改x:Name、Click、Command或Binding中的名称，它们连接程序逻辑。
- Text="{Binding StatusText}"属于动态内容，应到对应的C#文件修改赋给StatusText的文字。
- XAML属性中双引号、&和换行分别使用下面的实体，代码原文如下：

      &quot;
      &amp;
      &#xA;
      <TextBlock Text="第一行&#xA;第二行"/>

- C#普通字符串可以照下面的写法换行、包含双引号；模板的多行原始字符串可直接分行修改注释。

      "第一行\n第二行"
      "带有\"引号\"的提示"

- 改长文字后检查默认尺寸与最小尺寸下是否被截断；按钮适当调整MinWidth/Width或让说明TextWrapping="Wrap"。

## 预览与构建

在仓库根目录PowerShell执行：

    ./tools/Start-DevLauncher.ps1 -BuildOnly -CompilerPath D:/msys64/mingw32/bin/gcc.exe

结果在artifacts/dev/slot-a或slot-b。构建命令不打开窗口；执行下面命令可打开刚构建的预览：

    ./tools/Start-DevLauncher.ps1 -NoBuild -CompilerPath D:/msys64/mingw32/bin/gcc.exe

需要保存源码后自动构建预览时：

    ./tools/Watch-DevLauncher.ps1 -AutoRestart -CompilerPath D:/msys64/mingw32/bin/gcc.exe

自动预览会重启由监视器启动的开发面板，先结束该预览里的游戏。现有手动启动的其他包不自动重启。

只改文案不需要更换视觉组件，不改变版本发布权限。可交付ZIP仍按[构建规范](build-policy.md)输出artifacts/dev，正式版需要本次验收和明确授权。
