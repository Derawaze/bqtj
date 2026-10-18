using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using BqtjLauncher.Application;
using BqtjLauncher.Desktop;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;
using BqtjLauncher.Runtime.Flash;

/// <summary>真实脚本管理窗口的虚构数据回归与截图，不运行生产启动入口。</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3) return;
        var root = args[0]; var output = args[1]; Directory.CreateDirectory(output);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = XDocument.Load(Path.Combine(root, "src/BqtjLauncher.Desktop/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(ns + "ResourceDictionary",
            document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            document.Root.Element(ns + "Application.Resources")!.Elements()).ToString());
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Length == 3) await CheckVisionBridgeAsync(root, args[2]);
                await CheckAsync(root, output);
                await CheckDefaultDailyAsync(output);
                File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: account daily defaults, 2x4 first-run picker, cancel keeps scanned names/window without rewriting plans, persisted selection, nested setup, editor/monitor actions, templates, frozen run and default/minimum layouts");
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(output, "result.txt"), error.ToString()); Environment.ExitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
    }

    /// <summary>真实托管客户端连接原生视觉进程；只处理C++夹具自己创建的图像与窗口。</summary>
    private static async Task CheckVisionBridgeAsync(string root, string component)
    {
        var info = new ProcessStartInfo(Path.Combine(root, "artifacts/dev/probes/script-vision/ScriptVisionProbe.exe"), "--serve")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        using var fixture = Process.Start(info)!;
        try
        {
            var header = (await fixture.StandardOutput.ReadLineAsync())!.Split(' ');
            var pid = int.Parse(header[1], System.Globalization.CultureInfo.InvariantCulture);
            var window = long.Parse(header[2], System.Globalization.CultureInfo.InvariantCulture);
            var target = new AutomationWindowTarget(Guid.NewGuid(), Guid.NewGuid(), pid, pid, window, 950, 600);
            await using var vision = new SharedScriptVision(component);
            await vision.BeginAsync(default); await vision.BeginAsync(default);
            var frame = await vision.ReadAsync(target, new(240, 130, 480, 80), default);
            Require(frame.Text.Any(line => line.Text.Contains("读取存档", StringComparison.Ordinal)), "托管OCR协议");
            var pixels = await vision.PixelsAsync(target, new(400, 250, 40, 40), frame.SnapshotId, default);
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
            {
                writer.Write((ushort)0x4d42); writer.Write(54 + pixels.Bgr.Length); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(40); writer.Write(-40); writer.Write((ushort)1); writer.Write((ushort)24);
                writer.Write(new byte[24]); writer.Write(pixels.Bgr);
            }
            var box = await vision.FindImageAsync(target, new(380, 230, 90, 90), memory.ToArray(), default);
            Require(box is { X: 400, Y: 250, Width: 40, Height: 40 }, "托管模板结果坐标");
            await vision.ClickAsync(target, 410, 260, default);
            await vision.EndAsync();
            Require((await vision.ReadAsync(target, new(240, 130, 480, 80), default)).Text.Count > 0, "共享租用未提前退出");
            await vision.ReleaseTargetAsync(target); await vision.EndAsync();
        }
        finally
        {
            fixture.StandardInput.Close();
            if (!await Task.Run(() => fixture.WaitForExit(5000))) fixture.Kill(entireProcessTree: true);
        }
    }

    private static async Task CheckAsync(string root, string output)
    {
        var scripts = Path.Combine(output, "scripts"); Directory.CreateDirectory(Path.Combine(scripts, "official-daily"));
        File.Copy(Path.Combine(root, "resources/scripts/official-daily/script.json"), Path.Combine(scripts, "official-daily/script.json"), true);
        var repository = new SqliteGameProfileRepository(Path.Combine(output, "fake.db")); await repository.InitializeAsync();
        await repository.CreateAsync("同名虚构账号", "mock-a", "fake-a"); await repository.CreateAsync("同名虚构账号", "mock-b", "fake-b");
        await using var launcher = new LauncherModule(repository, new FakeRuntime());
        var executor = new FakeExecutor(); using var store = new ScriptFileStore(scripts);
        store.EnsureBuiltInPlans();
        await using var workspace = new ScriptWorkspace(store, repository, new ScriptCoordinator(launcher, executor));
        using var window = new ScriptManagerWindow(workspace); window.Show();
        var accounts = Get<ComboBox>(window, "Accounts");
        await UntilAsync(() => accounts.Items.Count == 2);
        Require(accounts.Items.Cast<ScriptAccount>().Select(account => account.Id).Distinct().Count() == 2, "同名账号身份");
        var library = Get<ListBox>(window, "PlansList");
        Require(library.Items.Count == 5 && library.Items.Cast<ScriptPlan>().Count(plan => plan.IsBuiltIn) == 3, "统一列表包含内置方案");
        Require(workspace.QuickPlans.All(plan => plan.DefaultAccountId is not null), "内置模板默认不出现在主面板");
        var builtin = workspace.Plans.First(plan => plan.IsBuiltIn);
        DoubleClickPlan(window, builtin.Id);
        var editor = Get<RichTextBox>(window, "SourceEditor");
        Require(!editor.IsReadOnly, "双击内置方案可直接编辑");
        SetSource(editor, "# 模板已直接编辑\n读取存档名称,稍后选择账号");
        Click(window, "保存方案");
        await UntilAsync(() => workspace.Plans.Single(plan => plan.Id == builtin.Id).Source.Contains("模板已直接编辑", StringComparison.Ordinal));
        Require(workspace.Plans.Single(plan => plan.Id == builtin.Id).IsBuiltIn, "模板编辑保留标记");
        Click(window, "新建方案");
        accounts.SelectedIndex = 1;
        var selected = (ScriptAccount)accounts.SelectedItem;
        workspace.SetSaveNames(selected.Id, Enumerable.Range(1, 8).Select(number => new SaveSlot(number, "测试存档" + number)).ToArray());
        Require(Get<ComboBox>(window, "SaveSlots").Items.Count == 8, "八档资料");
        Get<ComboBox>(window, "SaveSlots").SelectedIndex = 4;
        Get<TextBox>(window, "SaveName").Text = "手动校正 lv.99"; Click(window, "校正名称");
        Require(workspace.SaveNames[selected.Id][4].Name == "手动校正 lv.99", "名称校正持久化");
        Get<TextBox>(window, "PlanName").Text = "虚构账号多档方案";
        var commands = Get<ListBox>(window, "CommandsList");
        commands.SelectedItem = "日常操作";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        DoubleClick(commands, (DependencyObject)commands.ItemContainerGenerator.ContainerFromIndex(0));
        Get<ComboBox>(window, "SaveSlots").SelectedIndex = 5; Click(window, "插入语句");
        var insertedBeforeChecks = Source(editor);
        commands.SelectedItem = "读取存档名称";
        SetSource(editor, "");
        editor.CaretPosition = editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        Click(window, "插入语句");
        Require(Source(editor).Trim().StartsWith("读取存档名称,", StringComparison.Ordinal) && !Source(editor).StartsWith("\r\n", StringComparison.Ordinal), "空行直接插入本行");
        SetSource(editor, "原行\n\n后行");
        editor.CaretPosition = editor.Document.Blocks.OfType<Paragraph>().ElementAt(1).ContentStart;
        Click(window, "插入语句");
        Require(Source(editor).Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Length == 3, "中间空行复用不增加空行");
        editor.CaretPosition = editor.Document.Blocks.FirstBlock.ContentStart;
        Click(window, "插入语句");
        var afterInsert = Source(editor).Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        Require(afterInsert.Length == 4 && afterInsert[0] == "原行" && afterInsert[1].StartsWith("读取存档名称,", StringComparison.Ordinal), "非空行在下一行插入且不拆开原文");
        SetSource(editor, insertedBeforeChecks);
        Children<Expander>(window).Single().IsExpanded = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Get<TextBox>(window, "GlobalConstantsText").Text = "全局档=存档8";
        Click(window, "保存全局常量");
        Require(workspace.ReadGlobalConstants() == "全局档=存档8", "全局常量独立保存");
        var inserted = Source(editor);
        Require(inserted.Contains("存档5", StringComparison.Ordinal) && inserted.Contains("存档6", StringComparison.Ordinal), "双击命令与按钮均插入完整语句");
        Get<TextBox>(window, "ConstantsText").Text = "主号=" + Get<TextBox>(window, "AccountAlias").Text + "\n日常一=存档5\n日常二=存档6";
        var goodSource = "方案设置,子方案,是\n日常操作,$主号,$日常一\n日常操作,$主号,$日常二";
        SetSource(editor, goodSource);
        SetHeader(editor, "面板可见", false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Save(window, Path.Combine(output, "script-editor.png"));
        Click(window, "保存方案");
        await UntilAsync(() => workspace.Plans.Count == 6 && !Get<ScrollViewer>(window, "CommandsPanel").IsVisible);
        Require(editor.IsReadOnly && Get<Grid>(window, "MonitorPanel").IsVisible, "保存后命令栏隐藏并显示监控");
        var plan = workspace.Plans.Single(plan => plan.Name == "虚构账号多档方案");
        Require(plan.Version == 2 && plan.AccountBindings.ContainsValue(selected.Id), "文本与稳定账号绑定");
        Require(plan.Constants["日常二"] == "存档6", "常量保存");
        Require(workspace.QuickPlans.All(plan => plan.DefaultAccountId is not null), "保存时隐藏选项实际过滤主面板");
        DoubleClick(editor, editor);
        Require(!editor.IsReadOnly, "双击编辑区进入编辑");
        SetHeader(editor, "面板可见", true);
        Get<TextBox>(window, "PlanName").Text = "虚构子方案新名";
        var parent = new ScriptPlan { Version = 2, Name = "虚构主方案", Source = "调用,虚构账号多档方案", ShowOnDashboard = false };
        parent.PlanBindings[plan.Name] = plan.Id; workspace.Save(parent);
        Click(window, "保存方案");
        await UntilAsync(() => workspace.Plans.Any(candidate => candidate.Id == plan.Id && candidate.Name == "虚构子方案新名"));
        Require(workspace.Plans.Single(candidate => candidate.Id == parent.Id).Source == "调用,虚构子方案新名", "编辑名称同步其他方案调用");
        Require(workspace.QuickPlans.Single(candidate => candidate.DefaultAccountId is null).Id == plan.Id, "主面板只显示勾选的方案");

        // 错误语句及常量原文必须可保存，但运行校验失败不能创建会话或加载执行器。
        Click(window, "新建方案");
        Get<TextBox>(window, "PlanName").Text = "虚构未完成草稿";
        SetSource(editor, "日常操作,\"引号未写完");
        Get<TextBox>(window, "ConstantsText").Text = "还未填写等号和值";
        SetHeader(editor, "面板可见", false);
        Click(window, "保存方案");
        await UntilAsync(() => workspace.Plans.Any(candidate => candidate.Name == "虚构未完成草稿"));
        var broken = workspace.Plans.Single(candidate => candidate.Name == "虚构未完成草稿");
        Require(broken.ConstantsSource == "还未填写等号和值" && broken.Source.Contains("引号未写完", StringComparison.Ordinal), "错误草稿原文保留");
        Require(!string.IsNullOrWhiteSpace(Get<TextBlock>(window, "ValidationText").Text), "保存错误草稿仍显示错误");
        Click(window, "运行方案");
        await UntilAsync(() => Get<TextBlock>(window, "Status").Text.StartsWith("方案未启动", StringComparison.Ordinal));
        Require(executor.BeginCount == 0 && !workspace.Coordinator.IsRunning, "错误草稿没有执行副作用");
        DoubleClickPlan(window, plan.Id);
        Require(!editor.IsReadOnly, "双击已保存方案进入编辑");
        Require(!Children<Button>(window).Any(button => Equals(button.Content, "重命名") || Equals(button.Content, "运行监控")), "顶部监控与独立重命名按钮已移除");
        var openFolder = Get<Button>(window, "OpenScriptsButton");
        var label = new FormattedText((string)openFolder.Content, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(openFolder.FontFamily, openFolder.FontStyle, openFolder.FontWeight, openFolder.FontStretch),
            openFolder.FontSize, Brushes.White, VisualTreeHelper.GetDpi(openFolder).PixelsPerDip);
        Require(openFolder.ActualWidth >= label.WidthIncludingTrailingWhitespace + openFolder.Padding.Left + openFolder.Padding.Right + 2, "打开脚本文件夹文字完整显示");
        var workArea = SystemParameters.WorkArea;
        Require(window.Top >= workArea.Top && window.Top + window.ActualHeight <= workArea.Bottom + 1, "标题栏和关闭按钮位于工作区");
        Save(window, Path.Combine(output, "script-editor.png"));
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Save(window, Path.Combine(output, "script-editor-minimum.png"));
        Require(Get<Button>(window, "EditButton").IsVisible && Button(window, "运行方案").IsVisible, "最小窗口底部编辑与运行入口");
        Click(window, "运行方案"); await UntilAsync(() => executor.Entered.Task.IsCompleted && workspace.Coordinator.IsRunning);
        Save(window, Path.Combine(output, "script-running.png"));
        DoubleClick(editor, editor);
        editor.Document.Blocks.Add(new Paragraph(new Run("尚未完成的新语句")));
        Require(!Get<Button>(window, "StopButton").IsVisible && Get<Button>(window, "InsertButton").IsVisible, "编辑模式右下仅显示插入语句");
        Click(window, "保存方案");
        await UntilAsync(() => Get<Grid>(window, "MonitorPanel").IsVisible);
        Require(workspace.Coordinator.IsRunning, "保存错误草稿不修改当前运行快照");
        Require(Get<Button>(window, "StopButton").IsVisible && !Get<Button>(window, "InsertButton").IsVisible, "监控模式同位置显示停止");
        window.Close(); Require(!window.IsVisible && workspace.Coordinator.IsRunning, "隐藏不停止");
        window.Show(); executor.Hold.TrySetResult(); await UntilAsync(() => !workspace.Coordinator.IsRunning);
        Require(workspace.Coordinator.Updates.All(update => update.State == ScriptRunState.Completed), "运行仍按启动时的正确快照完成");
        DoubleClick(editor, editor);
        SetSource(editor, goodSource);
        Click(window, "保存方案");
        await UntilAsync(() => editor.IsReadOnly && string.IsNullOrWhiteSpace(Get<TextBlock>(window, "ValidationText").Text));
        Save(window, Path.Combine(output, "script-manager.png"));
        library.SelectedItem = workspace.Plans.Single(candidate => candidate.Id == builtin.Id);
        Click(window, "复制方案");
        Require(Get<TextBox>(window, "PlanName").Text == builtin.Name + " 副本", "统一列表复制方案");
        Require(Get<ScrollViewer>(window, "CommandsPanel").IsVisible && !editor.IsReadOnly, "副本进入编辑");
        Save(window, Path.Combine(output, "script-template-copy.png"));
        // 确认驱动只响应夹具自己进程的对话框，不会接触保留的游戏或生产窗口。
        var pendingName = Get<TextBox>(window, "PlanName").Text;
        var cancel = AnswerOwnDialogAsync(2); Click(window, "新建方案"); await cancel;
        Require(Get<TextBox>(window, "PlanName").Text == pendingName, "未保存选择取消留在草稿");
        var yes = AnswerOwnDialogAsync(6); Click(window, "新建方案"); await yes;
        await UntilAsync(() => Get<TextBox>(window, "PlanName").Text == "新方案");
        Require(workspace.Plans.Any(candidate => candidate.Name == pendingName), "未保存选择是先保存再切换");
        Get<TextBox>(window, "PlanName").Text = "不保存的虚构草稿";
        var no = AnswerOwnDialogAsync(7); Click(window, "新建方案"); await no;
        Require(Get<TextBox>(window, "PlanName").Text == "新方案" && !workspace.Plans.Any(candidate => candidate.Name == "不保存的虚构草稿"), "未保存选择否不写入草稿");
        await CheckMainAsync(root, output, workspace); window.Hide();
    }

    /// <summary>真实选档窗口及统一运行入口，使用独立虚构库检查首次选择、取消和记忆。</summary>
    private static async Task CheckDefaultDailyAsync(string output)
    {
        var repository = new SqliteGameProfileRepository(Path.Combine(output, "daily-fake.db"));
        await repository.InitializeAsync();
        await repository.CreateAsync("日常甲", "mock-daily-a", "fake-a");
        await repository.CreateAsync("日常乙", "mock-daily-b", "fake-b");
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(repository, runtime);
        var executor = new FakeExecutor(); executor.Hold.TrySetResult();
        using var store = new ScriptFileStore(Path.Combine(output, "daily-scripts"));
        await using var workspace = new ScriptWorkspace(store, new DelayedAccounts(repository), new ScriptCoordinator(launcher, executor));
        var allAccounts = await workspace.AccountsAsync();
        var daily = workspace.Plans.Single(plan => plan.DefaultAccountId == allAccounts[1].Id);
        var second = workspace.Plans.Single(plan => plan.DefaultAccountId == allAccounts[0].Id);
        Require(daily.DefaultAccountId != second.DefaultAccountId, "每个账号附带独立日常");
        workspace.SelectSaveAsync = _ => Task.FromResult<int?>(null);
        try { await workspace.RunAsync(daily); throw new InvalidOperationException("取消不应运行"); }
        catch (OperationCanceledException) { }
        Require(!workspace.IsBusy && runtime.StartedCount == 1 && executor.BeginCount == 1 && workspace.SaveNames.Count == 1, "取消保留扫描窗口和名称缓存");
        var root = new ScriptPlan { Version = 2, Source = "并行开始\n调用,甲\n调用,乙\n并行结束" };
        root.PlanBindings["甲"] = daily.Id; root.PlanBindings["乙"] = second.Id;
        var count = 0;
        workspace.SelectSaveAsync = _ => Task.FromResult<int?>(++count == 1 ? 3 : null);
        try { await workspace.RunAsync(root); throw new InvalidOperationException("子方案取消不应运行"); }
        catch (OperationCanceledException) { }
        Require(runtime.StartedCount == 2 && executor.BeginCount == 3
            && workspace.Plans.All(plan => plan.Source.Contains(AccountDailyPlans.PendingSave, StringComparison.Ordinal)), "子方案先收集全部选择，取消不部分回写");
        using var manager = new ScriptManagerWindow(workspace); manager.Show();
        await manager.OpenPlanAsync(daily.Id);
        await UntilAsync(() => Get<ComboBox>(manager, "Accounts").Items.Count == 2);
        Require(((ScriptAccount)Get<ComboBox>(manager, "Accounts").SelectedItem).Id == daily.DefaultAccountId, "编辑指定账号日常时参数选择器同步该账号");
        workspace.SelectSaveAsync = request =>
        {
            Require(workspace.SaveNames[request.Account.Id].Count == 8, "选档前已经自动读取八个名称");
            var picker = new SaveSlotSelectionWindow(request, workspace.SaveNames.GetValueOrDefault(request.Account.Id) ?? []) { Owner = manager };
            picker.Loaded += async (_, _) =>
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var cards = Children<Button>(picker).Where(button => button.Tag is int).ToArray();
                Require(cards.Select(card => (int)card.Tag).SequenceEqual(Enumerable.Range(1, 8)), "八张卡片顺序1～8");
                var points = cards.Select(card => card.TranslatePoint(new Point(), picker)).ToArray();
                Require(points[0].Y == points[1].Y && points[0].X < points[1].X && points[0].Y < points[2].Y
                    && points.Select(point => point.Y).Distinct().Count() == 4, "选档窗口为两列四行");
                Save(picker, Path.Combine(output, "daily-save-selection.png"));
                cards.Single(card => (int)card.Tag == 5).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            };
            return Task.FromResult(picker.ShowDialog() == true ? picker.SelectedSave : null);
        };
        Click(manager, "运行方案");
        await UntilAsync(() => executor.BeginCount == 5 && !workspace.IsBusy);
        var configured = workspace.Plans.Single(plan => plan.Id == daily.Id);
        Require(configured.Source.Contains("日常操作,本账号,存档5", StringComparison.Ordinal), "首次选择回写字面存档号");
        Require(Source(Get<RichTextBox>(manager, "SourceEditor")).Contains("存档5", StringComparison.Ordinal), "选档后只读编辑器同步保存源码");
        await launcher.CloseAllAsync();
        workspace.SelectSaveAsync = _ => throw new InvalidOperationException("已设置存档不应再询问");
        await workspace.RunAsync(configured);
        Require(executor.BeginCount == 6 && runtime.StartedCount == 3, "再次运行直接使用保存的存档");
        Click(manager, "复制方案"); Click(manager, "保存方案");
        await UntilAsync(() => workspace.Plans.Any(plan => plan.Name == configured.Name + " 副本"));
        Require(workspace.Plans.Single(plan => plan.Name == configured.Name + " 副本").DefaultAccountId is null, "复制日常不占用账号默认入口");
        Save(manager, Path.Combine(output, "account-daily-editor.png"));
        manager.Hide();
    }
    private static async Task CheckMainAsync(string root, string output, ScriptWorkspace workspace)
    {
        var xml = XDocument.Load(Path.Combine(root, "src/BqtjLauncher.Desktop/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var attribute in xml.Descendants().Attributes().Where(attribute => attribute.Name == x + "Class" ||
            attribute.Name.LocalName is "Click" or "SelectionChanged" or "RequestNavigate").ToArray()) attribute.Remove();
        foreach (var attribute in xml.Descendants().Attributes().Where(attribute => attribute.Name.LocalName == "SelectedItem"))
            attribute.Value = "{Binding SelectedProfile, Mode=OneWay}";
        var window = (Window)XamlReader.Parse(xml.ToString());
        var profiles = Enumerable.Range(0, 12).Select(index => new { DisplayName = "虚构账号" + index, LastLaunchedDisplay = "从未启动", RunningVisibility = Visibility.Collapsed }).ToArray();
        window.DataContext = new { Profiles = profiles, SelectedProfile = profiles[0], StatusText = "合成界面", UpdateStatusText = "开发版本" };
        Get<ComboBox>(window, "QuickScriptPlans").ItemsSource = workspace.QuickPlans.Where(plan => plan.DefaultAccountId is null).ToArray(); Get<ComboBox>(window, "QuickScriptPlans").SelectedIndex = 0;
        window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(Button(window, "管理脚本").IsVisible && Button(window, "运行日常").IsVisible && Button(window, "编辑日常").IsVisible, "当前账号日常为主要入口");
        Require(Get<ComboBox>(window, "QuickScriptPlans").Items.Cast<ScriptPlan>().All(plan => plan.ShowOnDashboard), "隐藏方案不进入主面板");
        Require(Get<TextBlock>(window, "ProjectLink").Inlines.OfType<Run>().Any(run => run.Text.Contains("Star", StringComparison.Ordinal)), "项目地址公开说明");
        Save(window, Path.Combine(output, "launcher-scripts.png"));
        window.Width = window.MinWidth; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Save(window, Path.Combine(output, "launcher-scripts-minimum.png"));
        Children<Expander>(window).Single(expander => Equals(expander.Header, "其他方案 / 多账号")).IsExpanded = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(Button(window, "运行所选方案").IsVisible, "其他方案折叠区可展开运行");
        Save(window, Path.Combine(output, "launcher-scripts-other-plans.png")); window.Close();
    }

    private static void SetHeader(RichTextBox editor, string key, bool value)
    {
        var lines = Source(editor).Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n').Split('\n')
            .Where(line => !line.StartsWith("方案设置," + key + ",", StringComparison.Ordinal));
        SetSource(editor, $"方案设置,{key},{(value ? "是" : "否")}\n" + string.Join("\n", lines));
    }

    /// <summary>仅响应当前夹具拥有的Win32确认框，用于保存/不保存/取消三条交互路径。</summary>
    private static Task AnswerOwnDialogAsync(int button) => Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            nint dialog = 0;
            EnumWindows((window, parameter) =>
            {
                _ = GetWindowThreadProcessId(window, out var owner);
                var name = new char[64]; var count = GetClassName(window, name, name.Length);
                if (owner == (uint)Environment.ProcessId && new string(name, 0, count) == "#32770") dialog = window;
                return true;
            }, 0);
            if (dialog != 0) { PostMessage(GetDlgItem(dialog, button), 0x00F5, 0, 0); return; }
            await Task.Delay(20);
        }
        throw new TimeoutException("夹具保存确认框未出现。");
    });
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, [Out] char[] buffer, int length);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    private static string Source(RichTextBox editor) => new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
    private static void SetSource(RichTextBox editor, string value) => new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text = value;
    /// <summary>目录监听会替换方案对象；双击按稳定ID重新定位当前项，不持有刷新前的容器。</summary>
    private static void DoubleClickPlan(Window window, Guid id)
    {
        var list = Get<ListBox>(window, "PlansList");
        list.SelectedItem = list.Items.Cast<ScriptPlan>().Single(plan => plan.Id == id);
        var current = list.Items.Cast<ScriptPlan>().Single(plan => plan.Id == id);
        list.ScrollIntoView(current);
        list.UpdateLayout();
        DoubleClick(list, (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(current));
    }
    private static void DoubleClick(Control control, DependencyObject source)
    {
        control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Control.MouseDoubleClickEvent, Source = source });
    }

    private static async Task UntilAsync(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!condition()) await Task.Delay(20, timeout.Token); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static T Get<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(Window window, string label) => Button(window, label).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static Button Button(DependencyObject root, string label) => Children<Button>(root).Single(button => Equals(button.Content, label));
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); if (child is T value) yield return value; foreach (var value2 in Children<T>(child)) yield return value2; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Save(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    /// <summary>故意延迟虚构账号查询，覆盖窗口首次显示时立即打开指定日常的交错。</summary>
    private sealed class DelayedAccounts(IScriptAccounts inner) : IScriptAccounts
    {
        public async Task<IReadOnlyList<ScriptAccount>> ListScriptAccountsAsync()
        {
            await Task.Delay(30);
            return await inner.ListScriptAccountsAsync();
        }
    }    private sealed class FakeRuntime : IAutomationGameRuntime
    {
        public Task<GamePageResolution> ResolveGamePageAsync() => throw new NotSupportedException();
        public int StartedCount { get; private set; }
        public Task<IGameSession> StartAsync(GameProfile profile, CancellationToken token = default)
        { StartedCount++; return Task.FromResult<IGameSession>(new FakeSession(profile.Id)); }
        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken token = default) => StartAsync(profile, token);
    }
    private sealed class FakeSession(Guid profile) : IAutomationGameSession
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid(); public Guid ProfileId => profile; public Task Completion => _closed.Task;
        public void Activate() { }
        public Task CloseAsync(CancellationToken token = default) { _closed.TrySetResult(); return Task.CompletedTask; }
        public bool WindowLocked { get; private set; } = true;
        public Task AcquireWindowLockAsync(CancellationToken token = default) => System.Threading.Tasks.Task.CompletedTask;
        public Task ReleaseWindowLockAsync(CancellationToken token = default) { WindowLocked = false; return System.Threading.Tasks.Task.CompletedTask; }
        public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken token = default) => Task.FromResult(SpeedMultiplier.Original);
    }
    private sealed class FakeExecutor : IScriptExecutor
    {
        public TaskCompletionSource Hold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BeginCount;
        public Task BeginAsync(CancellationToken token) { BeginCount++; return Task.CompletedTask; } public Task EndAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(AutomationGameSession session, Action<string> stage, CancellationToken token)
            => Task.FromResult<IReadOnlyList<SaveSlot>>(Enumerable.Range(1, 8).Select(number => new SaveSlot(number, "测试存档" + number)).ToArray());
        public async Task ExecuteAsync(AutomationGameSession session, ScriptDefinition definition, ScriptPlanTask task, SpeedMultiplier speed, Action<string, ScriptRunState> stage, CancellationToken token)
        { Entered.TrySetResult(); await Hold.Task.WaitAsync(token); }
        public Task ExitSaveAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token) => Task.CompletedTask;
        public Task ReleaseAsync(AutomationGameSession session) => Task.CompletedTask;
    }
}
