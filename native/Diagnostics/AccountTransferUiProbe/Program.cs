using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using BqtjLauncher.Application;
using BqtjLauncher.Desktop;
using BqtjLauncher.Infrastructure;

/// <summary>真实 WPF 合成回归：独立入口参数、新增提示、导入续行、跳过和人工替换。</summary>
internal static class Program
{
    private static AccountToolInstance _instance = null!;
    private static string _scope = string.Empty;
    private static int _activations;

    [STAThread]
    private static int Main(string[] args)
    {
        // 只使用显式探针目录，不调用 LocalApplicationData 或生产 App.OnStartup。
        var root = Environment.GetEnvironmentVariable("BQTJ_ACCOUNT_UI_ROOT")!;
        var output = Environment.GetEnvironmentVariable("BQTJ_ACCOUNT_UI_OUTPUT")!;
        Directory.CreateDirectory(output);
        _scope = Environment.GetEnvironmentVariable("BQTJ_ACCOUNT_UI_SCOPE")!;
        using var instance = AccountToolInstance.TryCreate(_scope);
        if (instance is null)
        {
            File.WriteAllText(Path.Combine(output, "secondary.txt"), "PASS: secondary exits and activates owner");
            return 0;
        }
        _instance = instance;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine(root, "src/BqtjLauncher.Desktop/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            source.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Require(args.Length == 1 && args[0] == "--account-tool", "独立入口模式参数");
                await RunAsync(output);
                File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: singleton, native and managed activation, add/edit entries, continued import, review, inline edits and rollback, search, multiselect deletion, themed scrolling");
                app.Shutdown(0);
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.Combine(output, "result.txt"), "FAIL: " + exception);
                app.Shutdown(1);
            }
        });
        return app.Run();
    }

    /// <summary>所有窗口和账号均为本进程夹具，不运行游戏，不访问生产数据。</summary>
    private static async Task RunAsync(string output)
    {
        var repository = new SqliteGameProfileRepository(Path.Combine(output, $"synthetic-{Guid.NewGuid():N}.db"));
        await repository.InitializeAsync();
        var id = await repository.CreateAsync("原记录", "fake-existing", "synthetic-old");
        var original = (await repository.GetAsync(id))!.MarkLaunched(DateTimeOffset.UtcNow);
        await repository.UpsertAsync(original);
        var editor = new AccountEditorWindow(repository, Guid.Empty, string.Empty);
        editor.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(((TextBlock)editor.FindName("PasswordHint")).Text.Contains("同时填写"), "新增密码提示");
        Require(((Button)editor.FindName("BatchToolButton")).IsVisible, "新增快捷入口");
        SaveImage(editor, Path.Combine(output, "add-account.png"));
        editor.Close();
        var existingEditor = new AccountEditorWindow(repository, id, "原记录");
        existingEditor.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(((TextBlock)existingEditor.FindName("PasswordHint")).Text.Contains("沿用原密码"), "已有密码提示");
        Require(((Button)existingEditor.FindName("BatchToolButton")).IsVisible, "编辑快捷入口");
        existingEditor.Close();
        var tool = new AccountTransferWindow(new AccountTransferService(repository));
        tool.Show();
        _instance.Listen(tool, () => { _activations++; tool.Reactivated(); });
        await WaitAsync(() => ((DataGrid)tool.FindName("Accounts")).Items.Count == 1);
        await CheckSingleInstanceAsync(tool, output);
        var input = (TextBox)tool.FindName("ImportText");
        input.Text = "新条 fake-first first-password\n重复 fake-existing rejected-password";
        Click(tool, "开始导入");
        await WaitAsync(() => System.Windows.Application.Current.Windows.OfType<AccountImportReviewWindow>().Any());
        Require((await repository.ListAsync()).Count == 2, "审核之前其他行已导入");
        var review = System.Windows.Application.Current.Windows.OfType<AccountImportReviewWindow>().Single();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveImage(review, Path.Combine(output, "duplicate-review.png"));
        var activation = _activations;
        Require(AccountToolInstance.TryActivate(_scope), "审核期间复用原工具");
        await WaitAsync(() => _activations > activation);
        Require(review.IsVisible && System.Windows.Application.Current.Windows.OfType<AccountImportReviewWindow>().Count() == 1, "原审核窗口仍在");
        Click(review, "跳过此条");
        await WaitAsync(() => !((Button)tool.FindName("ReviewButton")).IsEnabled);
        Require((await repository.ReadCredentialAsync(id))!.Password == "synthetic-old", "跳过保留原密码");
        input.Text = "新名称 fake-existing replacement-password\n新条 fake-second second-password";
        Click(tool, "开始导入");
        await WaitAsync(() => System.Windows.Application.Current.Windows.OfType<AccountImportReviewWindow>().Any());
        review = System.Windows.Application.Current.Windows.OfType<AccountImportReviewWindow>().Single();
        ((TextBox)review.FindName("DisplayNameInput")).Text = "人工复核";
        ((PasswordBox)review.FindName("PasswordInput")).Password = "synthetic-manual";
        Click(review, "替换原记录");
        await WaitAsync(() => !((Button)tool.FindName("ReviewButton")).IsEnabled &&
            ((DataGrid)tool.FindName("Accounts")).Items.Count == 3);
        var changed = (await repository.GetAsync(id))!;
        Require(changed.DisplayName == "人工复核" && changed.LastLaunchedAtUtc == original.LastLaunchedAtUtc &&
            changed.BrowserProfileName == original.BrowserProfileName, "替换保留记录身份和启动信息");
        Require((await repository.ReadCredentialAsync(id))!.Password == "synthetic-manual", "人工修改密码");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await CheckManagementAsync(tool, repository, id);
        SaveImage(tool, Path.Combine(output, "account-manager.png"));
        tool.Width = tool.MinWidth;
        tool.Height = tool.MinHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveImage(tool, Path.Combine(output, "account-manager-minimum.png"));
        await CheckPanelLayoutAsync(output);
        tool.Close();
    }

    /// <summary>实际次进程竞争互斥锁；C 小入口和托管快捷入口均只唤起首个窗口。</summary>
    private static async Task CheckSingleInstanceAsync(AccountTransferWindow tool, string output)
    {
        var baseline = _activations;
        using var secondary = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, Arguments = "--account-tool", WindowStyle = ProcessWindowStyle.Hidden })!;
        await secondary.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Require(secondary.ExitCode == 0 && File.Exists(Path.Combine(output, "secondary.txt")), "次进程退出");
        await WaitAsync(() => _activations > baseline && ((DataGrid)tool.FindName("Accounts")).IsEnabled);
        tool.WindowState = WindowState.Minimized;
        baseline = _activations;
        using var entry = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("BQTJ_ACCOUNT_UI_HELPER")!)
        { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
        await entry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(entry.ExitCode == 0, "原生入口正常退出");
        await WaitAsync(() => _activations > baseline && tool.WindowState != WindowState.Minimized && ((DataGrid)tool.FindName("Accounts")).IsEnabled);
        baseline = _activations;
        tool.WindowState = WindowState.Maximized;
        tool.WindowState = WindowState.Minimized;
        Require(AccountToolInstance.TryActivate(_scope), "托管入口激活已有窗口");
        await WaitAsync(() => _activations > baseline && ((DataGrid)tool.FindName("Accounts")).IsEnabled);
        Require(tool.WindowState == WindowState.Maximized, "唤起保留最大化状态");
        tool.WindowState = WindowState.Normal;
        Require(System.Windows.Application.Current.Windows.OfType<AccountTransferWindow>().Count() == 1, "只有一个工具窗口");
    }

    /// <summary>走实际双击/右键与提交事件，错误账号回滚，搜索结果决定批量选择范围。</summary>
    private static async Task CheckManagementAsync(AccountTransferWindow tool, SqliteGameProfileRepository repository, Guid id)
    {
        var grid = (DataGrid)tool.FindName("Accounts");
        object FindRow() => grid.Items.Cast<object>().Single(row => (Guid)row.GetType().GetProperty("Id")!.GetValue(row)! == id);
        await EditCellAsync(grid, FindRow(), 1, "原位名称", rightClick: false);
        Require((await repository.GetAsync(id))!.DisplayName == "原位名称", "双击名称保存");
        await EditCellAsync(grid, FindRow(), 2, "fake-edited", rightClick: true);
        Require((await repository.ReadCredentialAsync(id))!.Username == "fake-edited", "右键账号保存");
        var showPasswords = (CheckBox)tool.FindName("ShowPasswordsInput");
        Require(grid.Columns[3].Visibility == Visibility.Collapsed, "密码列默认隐藏");
        showPasswords.IsChecked = true;
        Require(grid.Columns[3].Visibility == Visibility.Visible, "显示密码开关展开密码列");
        await EditCellAsync(grid, FindRow(), 3, "edited-password", rightClick: false);
        Require((await repository.ReadCredentialAsync(id))!.Password == "edited-password", "明文密码保存");
        var edit = await BeginEditAsync(grid, FindRow(), 3, false);
        edit.Text = "must-not-save";
        grid.CancelEdit(DataGridEditingUnit.Cell);
        grid.CancelEdit(DataGridEditingUnit.Row);
        Require((await repository.ReadCredentialAsync(id))!.Password == "edited-password", "取消编辑保留密码");
        showPasswords.IsChecked = false;
        Require(grid.Columns[3].Visibility == Visibility.Collapsed, "隐藏密码列不改变凭据");
        showPasswords.IsChecked = true;
        await EditCellAsync(grid, FindRow(), 2, "fake-first", rightClick: true);
        Require((await repository.ReadCredentialAsync(id))!.Username == "fake-edited", "重复账号不保存");
        Require(((TextBlock)tool.FindName("Feedback")).Text.Contains("已存在"), "重复错误可见");
        var search = (TextBox)tool.FindName("SearchInput");
        search.Text = "FAKE-EDITED";
        Require(grid.Items.Count == 1, "账号搜索忽略大小写");
        ((CheckBox)tool.FindName("SelectAllInput")).IsChecked = true;
        Require(grid.SelectedItems.Count == 1, "全选只选当前搜索结果");
        search.Text = "原位名称";
        Require(grid.Items.Count == 1 && grid.SelectedItems.Count == 0, "名称搜索且清空旧选择");
        search.Clear();
        Require(grid.Items.Count == 3, "清空搜索显示全部");
        grid.SelectedItems.Add(grid.Items[1]);
        grid.ScrollIntoView(grid.Items[2]); grid.UpdateLayout();
        var third = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.Items[2]);
        var check = Descendants<CheckBox>(third).Single();
        check.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Require(grid.SelectedItems.Count == 2, "勾选不丢失其他选择");
        var keepId = (Guid)grid.Items[0].GetType().GetProperty("Id")!.GetValue(grid.Items[0])!;
        // 自动确认仅匹配本探针进程自己的 Win32 对话框，绝不操作其他窗口。
        var confirm = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var dialog = FindWindow("#32770", "确认批量删除");
                if (dialog != IntPtr.Zero && GetWindowThreadProcessId(dialog, out var owner) != 0 && owner == (uint)Environment.ProcessId)
                { PostMessage(GetDlgItem(dialog, 6), 0x00F5, IntPtr.Zero, IntPtr.Zero); return; }
                Thread.Sleep(20);
            }
            throw new TimeoutException("本探针删除确认未出现");
        });
        Click(tool, "删除所选");
        await confirm;
        await WaitAsync(() => grid.IsEnabled && grid.Items.Count == 1);
        Require((await repository.ListAsync()).Single().Id == keepId, "实际批量删除保留未选择账号");
        for (var index = 0; index < 35; index++) await repository.CreateAsync($"虚构账号{index:D2}", $"fake-scroll-{index:D2}", $"synthetic-{index:D2}");
        Click(tool, "刷新列表");
        await WaitAsync(() => grid.IsEnabled && grid.Items.Count == 36);
        grid.UpdateLayout();
        Require(Descendants<System.Windows.Controls.Primitives.ScrollBar>(grid).Any(bar => bar.IsVisible && bar.Orientation == Orientation.Vertical), "列表纵向滚动条可见");
    }

    private static async Task<TextBox> BeginEditAsync(DataGrid grid, object row, int column, bool rightClick)
    {
        grid.ScrollIntoView(row, grid.Columns[column]); grid.UpdateLayout();
        var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
        var cell = Descendants<DataGridCell>(container).Single(item => item.Column == grid.Columns[column]);
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, rightClick ? MouseButton.Right : MouseButton.Left)
        { RoutedEvent = rightClick ? UIElement.PreviewMouseRightButtonDownEvent : Control.PreviewMouseDoubleClickEvent, Source = cell });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(cell.IsEditing, "原位编辑框打开");
        return Descendants<TextBox>(cell).Single();
    }

    private static async Task EditCellAsync(DataGrid grid, object row, int column, string value, bool rightClick)
    {
        (await BeginEditAsync(grid, row, column, rightClick)).Text = value;
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        await WaitAsync(() => grid.IsEnabled && !Descendants<DataGridCell>(grid).Any(cell => cell.IsEditing));
    }

    /// <summary>仅解析实际面板布局，去掉事件并绑定虚构账号，避免启动生产服务或读取真实库。</summary>
    private static async Task CheckPanelLayoutAsync(string output)
    {
        var root = Environment.GetEnvironmentVariable("BQTJ_ACCOUNT_UI_ROOT")!;
        var panel = XDocument.Load(Path.Combine(root, "src/BqtjLauncher.Desktop/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        panel.Root!.Attribute(x + "Class")!.Remove();
        foreach (var attribute in panel.Descendants().Attributes().Where(attribute => attribute.Name.LocalName is "Click" or "RequestNavigate" or "SelectionChanged").ToArray()) attribute.Remove();
        panel.Root.SetAttributeValue("Icon", "/BqtjLauncher.Desktop;component/Assets/launcher.ico");
        foreach (var image in panel.Descendants().Where(element => element.Name.LocalName == "Image")) image.SetAttributeValue("Source", "/BqtjLauncher.Desktop;component/Assets/launcher.png");
        foreach (var list in panel.Descendants().Where(element => element.Name.LocalName == "ListBox")) list.SetAttributeValue("SelectedItem", "{Binding SelectedProfile, Mode=OneWay}");
        var window = (Window)XamlReader.Parse(panel.ToString());
        var rows = Enumerable.Range(0, 30).Select(index => new { DisplayName = $"虚构账号{index}", LastLaunchedDisplay = "从未启动", RunningVisibility = Visibility.Collapsed }).ToArray();
        window.DataContext = new { Profiles = rows, SelectedProfile = rows[0], StatusText = "合成界面检查", UpdateStatusText = "开发模式" };
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(FindButton(window, "管理账号") is { IsVisible: true }, "面板管理按钮可见");
        SaveImage(window, Path.Combine(output, "launcher-panel.png"));
        window.Width = window.MinWidth;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SaveImage(window, Path.Combine(output, "launcher-panel-minimum.png"));
        window.Close();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr handle, int id);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }

    /// <summary>只遍历夹具自身的可视树，不选中其他应用或用户游戏。</summary>
    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && Equals(button.Content, content)) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindButton(VisualTreeHelper.GetChild(root, index), content) is { } found) return found;
        return null;
    }

    private static void Click(DependencyObject root, string content) =>
        (FindButton(root, content) ?? throw new InvalidOperationException("未找到夹具按钮：" + content))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>图片只包含本探针的虚构账号，用于检查布局。</summary>
    private static void SaveImage(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
