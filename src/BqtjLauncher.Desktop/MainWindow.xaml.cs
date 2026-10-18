using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using BqtjLauncher.Infrastructure;
using Microsoft.Win32;

namespace BqtjLauncher.Desktop;

/// <summary>
/// 管理面板主窗口，负责初始化展示模型与需要用户确认的窗口级操作。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly BqtjLauncher.Application.IAccountEditor _accountEditor;
    private bool _ready;
    private bool _refreshing;
    private readonly ScriptWorkspace? _scripts;
    private ScriptManagerWindow? _scriptWindow;
    private Guid? _quickPlanId;

    /// <summary>只打开日志子目录，避免引导用户上传包含明文凭据的整个数据目录。</summary>
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticBundleExporter.LogDirectory);
            Process.Start(new ProcessStartInfo(DiagnosticBundleExporter.LogDirectory) { UseShellExecute = true });
            _viewModel.StatusText = "已打开日志目录；反馈问题建议使用脱敏诊断包。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _viewModel.StatusText = "无法打开日志目录，请检查目录权限。";
        }
    }

    /// <summary>用户选择保存位置后后台生成摘要包；取消不产生文件，不自动上传。</summary>
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存诊断包（仅环境信息与脱敏日志摘要）",
            Filter = "ZIP 诊断包|*.zip",
            DefaultExt = ".zip",
            FileName = $"Bqtj-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportDiagnosticsButton.IsEnabled = false;
        _viewModel.StatusText = "正在生成诊断包…";
        try
        {
            var count = await Task.Run(() => DiagnosticBundleExporter.Export(
                DiagnosticBundleExporter.LogDirectory, dialog.FileName, _viewModel.CurrentLauncherVersion));
            _viewModel.StatusText = $"诊断包已保存（{count} 份日志摘要）：{dialog.FileName}。请附上复现步骤和发生时间。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _viewModel.StatusText = "诊断包导出失败，请选择可写入的本地目录后重试。";
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    public MainWindow(MainWindowViewModel viewModel, BqtjLauncher.Application.IAccountEditor accountEditor, ScriptWorkspace? scripts = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _accountEditor = accountEditor;
        _scripts = scripts;
        DataContext = viewModel;
        if (_scripts is not null)
        {
            _scripts.SelectSaveAsync = SelectSaveAsync;
            _scripts.Changed += Scripts_Changed;
            Scripts_Changed(this, EventArgs.Empty);
        }
        _viewModel.PropertyChanged += SelectedProfile_Changed;
        Loaded += async (_, _) => { await _viewModel.InitializeAsync(); await RefreshScriptsAsync(); _ready = true; };
        Activated += MainWindow_Activated;
        // 首次显示后启动独立异步检查，不等待游戏入口解析，也不在账号容器中重复检查。
        ContentRendered += MainWindow_ContentRendered;
        Closed += (_, _) => { _viewModel.CheckForUpdatesCommand.Cancel(); _scriptWindow?.Dispose(); };
    }

    /// <summary>非模态脚本窗口复用同一实例，不创建第二份账号会话管理或运行队列。</summary>
    private void ManageScripts_Click(object sender, RoutedEventArgs args)
    {
        if (_scripts is null) return;
        _scriptWindow ??= new ScriptManagerWindow(_scripts);
        _scriptWindow.Show();
        if (_scriptWindow.WindowState == WindowState.Minimized) _scriptWindow.WindowState = WindowState.Normal;
        _scriptWindow.Activate();
    }

    private void SelectedProfile_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.SelectedProfile)) Scripts_Changed(this, EventArgs.Empty);
    }

    private BqtjLauncher.Application.ScriptPlan? SelectedDailyPlan => _scripts?.Plans.FirstOrDefault(
        plan => plan.DefaultAccountId == _viewModel.SelectedProfile?.Id && plan.DefaultAccountId is not null && plan.ShowOnDashboard);

    /// <summary>当前账号日常始终单独展示，多账号和其他方案收在次级区域。</summary>
    private void Scripts_Changed(object? sender, EventArgs args)
    {
        if (_scripts is null) return;
        var others = _scripts.QuickPlans.Where(plan => plan.DefaultAccountId is null).ToArray();
        QuickScriptPlans.ItemsSource = others;
        QuickScriptPlans.SelectedItem = others.FirstOrDefault(plan => plan.Id == _quickPlanId) ?? others.FirstOrDefault();
        RunScriptButton.IsEnabled = !_scripts.IsBusy && QuickScriptPlans.SelectedItem is not null;
        var daily = SelectedDailyPlan;
        RunDailyButton.IsEnabled = daily is not null && !_scripts.IsBusy;
        EditDailyButton.IsEnabled = daily is not null;
        DailyScriptStatus.Text = daily is null ? "该账号暂无面板可用的日常；删除或隐藏后可在脚本管理中配置。"
            : "按本方案运行；首次选择存档后会记住，“编辑日常”可修改。";
        ScriptQueueStatus.Text = _scripts.Coordinator.IsRunning ? "正在运行：" + _scripts.Coordinator.RunningName + "。在脚本管理中查看进度。"
            : _scripts.IsBusy ? _scripts.PreparationStage
            : others.Length == 0 ? "其他方案可在脚本管理中设置“面板可见,是”。" : "";
    }

    private async Task RefreshScriptsAsync()
    {
        if (_scripts is null) return;
        try { await _scripts.AccountsAsync(); Scripts_Changed(this, EventArgs.Empty); }
        catch (Exception) { _viewModel.StatusText = "默认日常准备失败，请检查脚本目录或稍后重试。"; }
    }

    /// <summary>所有运行入口共用同一选档弹窗，选择前不创建游戏或读取真实存档。</summary>
    private Task<int?> SelectSaveAsync(BqtjLauncher.Application.ScriptSaveSelection request)
    {
        var owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? this;
        var dialog = new SaveSlotSelectionWindow(request, _scripts!.SaveNames.GetValueOrDefault(request.Account.Id) ?? []) { Owner = owner };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.SelectedSave : null);
    }

    private void QuickScriptSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs args)
    {
        if (QuickScriptPlans.SelectedItem is BqtjLauncher.Application.ScriptPlan plan) _quickPlanId = plan.Id;
        if (RunScriptButton is not null) RunScriptButton.IsEnabled = _scripts is not null && !_scripts.IsBusy && QuickScriptPlans.SelectedItem is not null;
    }

    private async void RunDaily_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedDailyPlan is { } plan) await RunPlanAsync(plan);
    }

    private async void EditDaily_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedDailyPlan is not { } plan) return;
        ManageScripts_Click(sender, args);
        if (_scriptWindow is not null) await _scriptWindow.OpenPlanAsync(plan.Id);
    }

    private async void RunScript_Click(object sender, RoutedEventArgs args)
    {
        if (QuickScriptPlans.SelectedItem is BqtjLauncher.Application.ScriptPlan plan) await RunPlanAsync(plan);
    }

    private async Task RunPlanAsync(BqtjLauncher.Application.ScriptPlan plan)
    {
        if (_scripts is null) return;
        try { await _scripts.RunAsync(plan); }
        catch (OperationCanceledException) { _viewModel.StatusText = "已取消选择存档，脚本未启动。"; }
        catch (Exception error) { _viewModel.StatusText = error.Message; }
        finally { Scripts_Changed(this, EventArgs.Empty); }
    }
    /// <summary>从独立工具返回面板时更新账号列表，保留当前选中项，不重新查询游戏版本。</summary>
    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (!_ready || _refreshing || _viewModel.IsBusy) return;
        _refreshing = true;
        try { await _viewModel.RefreshAsync(); await RefreshScriptsAsync(); }
        catch (Exception) { _viewModel.StatusText = "账号列表更新失败，请稍后重试。"; }
        finally { _refreshing = false; }
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        _viewModel.CheckForUpdatesCommand.Execute(null);
    }

    /// <summary>由用户主动打开已核实的发布页，下载交由浏览器处理。</summary>
    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AvailableRelease is not { } release) return;
        try
        {
            Process.Start(new ProcessStartInfo(release.ReleaseUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _viewModel.UpdateStatusText = $"无法打开浏览器，请手动访问 {release.ReleaseUri}";
        }
    }

    /// <summary>新增复用编辑表单，确认后原子创建账号和凭据，取消不留记录。</summary>
    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var dialog = new AccountEditorWindow(_accountEditor, Guid.Empty, string.Empty) { Owner = this };
        var saved = dialog.ShowDialog() == true;
        if (saved || dialog.AccountToolOpened)
        {
            await _viewModel.RefreshAsync(saved ? dialog.SavedAccountId : null);
            await RefreshScriptsAsync();
        }
    }

    /// <summary>固定打开时的账号 ID，避免列表刷新后编辑到另一账号。</summary>
    private async void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedProfile;
        if (selected is null || _viewModel.IsBusy) return;
        var dialog = new AccountEditorWindow(_accountEditor, selected.Id, selected.DisplayName) { Owner = this };
        var saved = dialog.ShowDialog() == true;
        if (saved || dialog.AccountToolOpened) { await _viewModel.RefreshAsync(selected.Id); await RefreshScriptsAsync(); }
    }

    /// <summary>直接打开或激活同一个账号管理工具，独立进程不依附面板生命周期。</summary>
    private void ManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        try { AccountToolLauncher.Open(); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        { _viewModel.StatusText = "无法打开账号管理工具，请尝试双击包内 BqtjAccountTool.exe。"; }
    }
    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProfile is null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"删除账号“{_viewModel.SelectedProfile.DisplayName}”？这会删除本地账号记录及保存的账号密码。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteSelectedAsync();
        }
    }

    /// <summary>仅响应用户主动点击，用默认浏览器打开固定项目地址。</summary>
    private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Derawaze/bqtj") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "无法打开浏览器，请手动访问 https://github.com/Derawaze/bqtj", "项目地址");
        }
    }
}
