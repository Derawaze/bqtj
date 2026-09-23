using System.Collections.ObjectModel;
using System.Windows;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;
using BqtjLauncher.Runtime.Flash;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BqtjLauncher.Desktop;

/// <summary>
/// 管理面板的展示模型，协调账号列表、启动操作和用户可见状态。
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LauncherModule _launcher;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;

    [ObservableProperty]
    private string _statusText = "正在准备…";

    [ObservableProperty]
    private bool _isBusy;

    public MainWindowViewModel(
        LauncherModule launcher,
        ILogger<MainWindowViewModel> logger)
    {
        _launcher = launcher;
        _logger = logger;
        _launcher.SessionsChanged += Launcher_SessionsChanged;
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        var flash = FlashRuntimeDetector.Detect32Bit();
        // 环境正常时隐藏 OCX 路径等实现细节；异常时保留检测器原文以便用户排查。
        StatusText = !flash.IsAvailable
            ? flash.Message
            : Profiles.Count == 0
                ? "准备就绪，请先新增账号。"
                : $"准备就绪 · {Profiles.Count} 个账号";
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        await RunBusyAsync(async () =>
        {
            var profile = await _launcher.CreateProfileAsync(NewProfileName);
            NewProfileName = string.Empty;
            await RefreshAsync(profile.Id);
            StatusText = $"已创建账号“{profile.DisplayName}”。";
        });
    }

    [RelayCommand]
    private async Task RenameProfileAsync()
    {
        if (SelectedProfile is null)
        {
            StatusText = "请先选择一个账号。";
            return;
        }

        await RunBusyAsync(async () =>
        {
            var profile = await _launcher.RenameProfileAsync(SelectedProfile.Id, NewProfileName);
            NewProfileName = string.Empty;
            await RefreshAsync(profile.Id);
            StatusText = $"账号已重命名为“{profile.DisplayName}”。";
        });
    }

    [RelayCommand]
    private async Task LaunchProfileAsync()
    {
        if (SelectedProfile is null)
        {
            StatusText = "请先选择一个账号。";
            return;
        }

        var selectedId = SelectedProfile.Id;
        await RunBusyAsync(async () =>
        {
            try
            {
                await _launcher.StartAsync(selectedId);
                StatusText = "游戏窗口已启动，正在加载…";
            }
            catch (ProfileAlreadyRunningException)
            {
                StatusText = "该账号已经在运行，已尝试聚焦现有窗口。";
            }

            await RefreshAsync(selectedId);
        });
    }

    [RelayCommand]
    private async Task RestartProfileAsync()
    {
        if (SelectedProfile is null)
        {
            StatusText = "请先选择一个账号。";
            return;
        }

        if (!SelectedProfile.IsRunning)
        {
            StatusText = "该账号当前没有正在运行的游戏容器。";
            return;
        }

        var selectedId = SelectedProfile.Id;
        await RunBusyAsync(async () =>
        {
            StatusText = "正在重启游戏窗口…";
            await _launcher.RestartAsync(selectedId);
            await RefreshAsync(selectedId);
            StatusText = "游戏窗口已重新启动。";
        });
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var displayName = SelectedProfile.DisplayName;
        await RunBusyAsync(async () =>
        {
            await _launcher.DeleteProfileAsync(SelectedProfile.Id);
            await RefreshAsync();
            StatusText = $"已删除账号“{displayName}”。";
        });
    }

    public async Task RefreshAsync(Guid? preferredSelection = null)
    {
        var selectedId = preferredSelection ?? SelectedProfile?.Id;
        var profiles = await _launcher.ListProfilesAsync();
        var activeIds = await _launcher.GetActiveProfileIdsAsync();

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new ProfileItemViewModel(profile, activeIds.Contains(profile.Id)));
        }

        SelectedProfile = selectedId is null
            ? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? Profiles.FirstOrDefault();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            LogOperationFailed(_logger, exception);
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Launcher_SessionsChanged(object? sender, EventArgs e)
    {
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(
            async () => await RefreshAsync());
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "启动器操作失败")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception);
}

/// <summary>
/// 为账号列表提供适合直接绑定的状态与本地时间文案。
/// </summary>
public sealed class ProfileItemViewModel
{
    public ProfileItemViewModel(GameProfile profile, bool isRunning)
    {
        Id = profile.Id;
        DisplayName = profile.DisplayName;
        IsRunning = isRunning;
        LastLaunchedDisplay = profile.LastLaunchedAtUtc is null
            ? "尚未启动"
            : $"上次启动：{profile.LastLaunchedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public bool IsRunning { get; }

    public string LastLaunchedDisplay { get; }

    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
}
