using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace BqtjLauncher.Desktop;

/// <summary>
/// 管理面板主窗口，负责初始化展示模型与需要用户确认的窗口级操作。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly BqtjLauncher.Application.IAccountEditor _accountEditor;

    public MainWindow(MainWindowViewModel viewModel, BqtjLauncher.Application.IAccountEditor accountEditor)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _accountEditor = accountEditor;
        DataContext = viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        // 首次显示后启动独立异步检查，不等待游戏入口解析，也不在账号容器中重复检查。
        ContentRendered += MainWindow_ContentRendered;
        Closed += (_, _) => _viewModel.CheckForUpdatesCommand.Cancel();
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
        if (dialog.ShowDialog() != true) return;
        await _viewModel.RefreshAsync(dialog.SavedAccountId);
    }

    /// <summary>固定打开时的账号 ID，避免列表刷新后编辑到另一账号。</summary>
    private async void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedProfile;
        if (selected is null || _viewModel.IsBusy) return;
        var dialog = new AccountEditorWindow(_accountEditor, selected.Id, selected.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true) await _viewModel.RefreshAsync(selected.Id);
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
