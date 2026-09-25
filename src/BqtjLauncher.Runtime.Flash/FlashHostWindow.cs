using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BqtjLauncher.Domain;
using WinFormsDockStyle = System.Windows.Forms.DockStyle;
using WinFormsPanel = System.Windows.Forms.Panel;
using WinFormsScreen = System.Windows.Forms.Screen;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSeparator = System.Windows.Controls.Separator;

namespace BqtjLauncher.Runtime.Flash;

public sealed class FlashHostWindow : Window, IDisposable
{
    internal const int NativeGameWidth = 950;
    internal const int NativeGameHeight = 600;
    private readonly NativeFlashHostController _flashHost;
    private readonly BqtjLauncher.Application.AccountCredential? _credential;
    private readonly Func<Task<BqtjLauncher.Application.AccountCredential?>>? _readCredential;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SpeedPreferenceStore _speedPreferenceStore;
    private readonly SpeedSelectionHistory _speedHistory;
    private readonly GameRuntimePreferenceStore _runtimePreferenceStore;
    private readonly WinFormsPanel _surface = new()
    {
        Dock = WinFormsDockStyle.Fill,
        BackColor = System.Drawing.Color.Black,
    };
    private readonly int _panelProcessId;
    private readonly string _baseTitle;
    private readonly bool _layoutProbeEnabled;
    private readonly Dictionary<decimal, WpfMenuItem> _speedItems = [];
    private readonly Dictionary<decimal, WpfMenuItem> _scaleItems = [];
    private WpfButton? _speedButton;
    private WpfButton? _muteButton;
    private WpfButton? _reloadButton;
    private WindowsFormsHost? _gameHost;
    private TextBlock? _loadingText;
    private Grid? _gameArea;
    private WpfMenuItem? _recentSpeedItem;
    private GameRuntimePreferences _runtimePreferences;
    private decimal _windowScale;
    private decimal _startupAppliedScale;
    private bool _isFullScreen;
    private bool _disposed;

    public FlashHostWindow(
        Guid accountId,
        string accountName,
        FlashRuntimeOptions options,
        int panelProcessId,
        bool layoutProbeEnabled = false,
        bool isolationCompatibilityAudioDisabled = false,
        BqtjLauncher.Application.AccountCredential? credential = null,
        Func<Task<BqtjLauncher.Application.AccountCredential?>>? readCredential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentOutOfRangeException.ThrowIfLessThan(panelProcessId, 1);
        options.Validate();
        _credential = credential;
        _readCredential = readCredential;
        _flashHost = new NativeFlashHostController(
            options.GamePageUri,
            audioControlEnabled: !isolationCompatibilityAudioDisabled);
        _speedPreferenceStore = new SpeedPreferenceStore(accountId);
        _speedHistory = new SpeedSelectionHistory(_speedPreferenceStore.LoadRecent());
        _runtimePreferenceStore = new GameRuntimePreferenceStore();
        _runtimePreferences = _runtimePreferenceStore.Load();
        _panelProcessId = panelProcessId;
        _baseTitle = $"爆枪突击 - {accountName}";
        _layoutProbeEnabled = layoutProbeEnabled;
        Title = _baseTitle;
        var workArea = WinFormsScreen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var startupLayout = GameWindowStartupLayout.Calculate(
            _runtimePreferences.WindowMode,
            options.WindowWidth,
            options.WindowHeight,
            workArea.Width,
            workArea.Height,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        _windowScale = startupLayout.RequestedScale;
        _startupAppliedScale = startupLayout.AppliedScale;
        _isFullScreen = startupLayout.IsFullScreen;
        Width = startupLayout.Width;
        Height = startupLayout.Height;
        MinWidth = 500;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = WpfBrushes.Black;
        if (_isFullScreen)
        {
            // 在 Show() 前进入最大化无边框状态，避免先绘制普通窗口再切换全屏。
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }

        _surface.Resize += (_, _) =>
        {
            _flashHost.Resize(_surface.ClientSize.Width, _surface.ClientSize.Height);
            UpdateLayoutProbeTitle();
        };
        PreviewKeyDown += OnPreviewKeyDown;

        Content = BuildLayout();
    }

    /// <summary>先校准真实客户区，显示加载提示，页面就绪后才显示原生游戏表面。</summary>
    public async Task StartAsync()
    {
        _surface.CreateControl();

        if (_isFullScreen)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (_surface.ClientSize.Width > 0 && _surface.ClientSize.Height > 0)
            {
                _startupAppliedScale = Math.Min(
                    (decimal)_surface.ClientSize.Width / NativeGameWidth,
                    (decimal)_surface.ClientSize.Height / NativeGameHeight);
            }
        }
        else
        {
            /*
             * 构造期只能预估 WPF 窗框；高 DPI 下 WindowsFormsHost 的真实像素尺寸可能不同。
             * 必须在创建原生浏览器前校准，否则固定 950×600 页面会在右侧和底部留下白边。
             */
            var targetWidth = Math.Max(
                1,
                (int)Math.Floor(NativeGameWidth * _startupAppliedScale));
            var targetHeight = Math.Max(
                1,
                (int)Math.Floor(NativeGameHeight * _startupAppliedScale));
            var actualSize = await FitClientAreaAsync(targetWidth, targetHeight);
            _startupAppliedScale = GameWindowSizing.CalculateContainedScale(
                actualSize.Width,
                actualSize.Height,
                NativeGameWidth,
                NativeGameHeight,
                _startupAppliedScale);
        }

        _flashHost.Start(
            _surface.Handle,
            _surface.ClientSize.Width,
            _surface.ClientSize.Height,
            _startupAppliedScale);

        _flashHost.ConfigureCredential(_credential);
        _flashHost.SetMuted(_runtimePreferences.IsMuted);

        // 加载阶段隐藏整个 HwndHost，而非用 WPF 遮罩盖住会穿透的 ActiveX 子窗口。
        Opacity = 1;
        ShowActivated = true;
        Activate();
        try
        {
            await StabilizeAndShowAsync(_lifetime.Token);
            if (!_disposed && _reloadButton is not null)
            {
                _reloadButton.IsEnabled = true;
            }
        }
        catch (Exception exception) when (
            _lifetime.IsCancellationRequested
            && exception is OperationCanceledException
                or ObjectDisposedException
                or InvalidOperationException
                or IOException)
        {
            // 用户在加载期关闭窗口属于正常取消，不应误报“启动器初始化失败”。
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _flashHost.Dispose();
        _surface.Dispose();
        // 异步的启动/刷新收尾可能仍会读取取消状态；窗口关闭时只发出取消信号，
        // 避免提前释放令牌源导致异常筛选器在关闭路径上再次抛错。
        GC.SuppressFinalize(this);
    }

    private void UpdateLayoutProbeTitle()
    {
        if (_surface.ClientSize.Width <= 0 || _surface.ClientSize.Height <= 0)
        {
            return;
        }

        if (_layoutProbeEnabled)
        {
            Title = $"{_baseTitle} [layout:{_surface.ClientSize.Width}x{_surface.ClientSize.Height}]";
        }
    }

    private DockPanel BuildLayout()
    {
        _reloadButton = CreateButton("刷新游戏");
        _reloadButton.IsEnabled = false;
        _reloadButton.Click += async (_, _) =>
        {
            try
            {
                _reloadButton.IsEnabled = false;
                await ReloadAndRevealAsync(_lifetime.Token);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or ObjectDisposedException
                    or IOException
                    or TimeoutException)
            {
                if (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                WpfMessageBox.Show(
                    this,
                    exception.Message,
                    "刷新失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (!_disposed && _reloadButton is not null)
                {
                    _reloadButton.IsEnabled = true;
                }
            }
        };

        _speedButton = CreateButton("变速");
        _speedButton.ToolTip = "本地播放倍率；F3 与上一档倍率互换";
        var speedMenu = CreateSpeedMenu();
        _speedButton.Click += (_, _) =>
        {
            speedMenu.PlacementTarget = _speedButton;
            speedMenu.IsOpen = true;
        };

        var scale = CreateButton("窗口");
        scale.ToolTip = "按游戏原生 950×600 等比调整画面和窗口";
        var scaleMenu = CreateScaleMenu();
        scale.Click += (_, _) =>
        {
            scaleMenu.PlacementTarget = scale;
            scaleMenu.IsOpen = true;
        };

        var back = CreateButton("返回面板");
        back.Click += async (_, _) =>
        {
            await ActivatePanelAsync();
            Close();
        };

        _muteButton = CreateButton(string.Empty);
        _muteButton.ToolTip = "仅静音游戏，不影响系统和其他程序";
        _muteButton.Click += (_, _) => ToggleMuted();
        UpdateMuteButton();

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Left,
        };
        buttons.Children.Add(_reloadButton);
        buttons.Children.Add(_speedButton);
        buttons.Children.Add(scale);
        buttons.Children.Add(_muteButton);
        var accountInfo = CreateButton("账号密码");
        accountInfo.Click += async (_, _) =>
        {
            try
            {
                var saved = _readCredential is null ? _credential : await _readCredential();
                if (!_disposed) new AccountCredentialWindow(saved) { Owner = this }.ShowDialog();
            }
            catch (Exception) { WpfMessageBox.Show(this, "无法读取当前账号信息，请稍后重试。", "账号密码"); }
        };
        buttons.Children.Add(accountInfo);
        buttons.Children.Add(back);

        var toolbar = new Grid
        {
            Height = 42,
            Background = new SolidColorBrush(WpfColor.FromRgb(28, 33, 43)),
        };
        toolbar.Children.Add(buttons);

        _gameHost = new WindowsFormsHost { Child = _surface, Visibility = Visibility.Hidden };
        _loadingText = new TextBlock
        {
            Text = "正在加载游戏…",
            Foreground = WpfBrushes.LightGray,
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 16,
        };
        _gameArea = new Grid { Background = new SolidColorBrush(WpfColor.FromRgb(20, 25, 34)) };
        _gameArea.Children.Add(_loadingText);
        _gameArea.Children.Add(_gameHost);
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_gameArea);
        return root;
    }

    /// <summary>串行完成“等待稳定并显示”，防止旧刷新计时器提前显示新浏览器。</summary>
    private async Task StabilizeAndShowAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            await _flashHost.WaitForDisplayReadyAsync(cancellationToken);
            await HoldBlackFrameAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _flashHost.ShowAsync();
            SetLoading(false);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>刷新与其稳定等待属于同一事务；下一次刷新必须等本次显示完成。</summary>
    private async Task ReloadAndRevealAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetLoading(true);
            await _flashHost.ReloadAsync();
            await _flashHost.WaitForDisplayReadyAsync(cancellationToken);
            await HoldBlackFrameAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _flashHost.ShowAsync();
            SetLoading(false);
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested
            && exception is OperationCanceledException
                or ObjectDisposedException
                or InvalidOperationException
                or IOException)
        {
            // 关闭窗口会终止原生宿主和管道；此时安静结束刷新事务。
        }
        finally
        {
            if (!_disposed) SetLoading(false);
            _loadGate.Release();
        }
    }

    /// <summary>按用户要求在揭开画面前固定展示一秒黑底，冷启动与刷新共用且支持关闭取消。</summary>
    private async Task HoldBlackFrameAsync(CancellationToken cancellationToken)
    {
        if (_gameArea is not null) _gameArea.Background = WpfBrushes.Black;
        if (_loadingText is not null) _loadingText.Visibility = Visibility.Collapsed;
        // 先让黑底真正参与一次渲染，再开始计时；原生父窗口继续隐藏，避免 ActiveX 穿透。
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    /// <summary>隐藏原生窗口期间保留布局尺寸；网络加载提示与揭开前的一秒黑底分别处理。</summary>
    private void SetLoading(bool loading)
    {
        if (_gameHost is not null) _gameHost.Visibility = loading ? Visibility.Hidden : Visibility.Visible;
        if (_loadingText is not null) _loadingText.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading && _gameArea is not null) _gameArea.Background = new SolidColorBrush(WpfColor.FromRgb(20, 25, 34));
    }

    private WpfContextMenu CreateSpeedMenu()
    {
        var menu = new WpfContextMenu();
        AddSpeedMenuItem(menu, "原速", SpeedMultiplier.Original);
        AddSpeedMenuItem(menu, "0.03倍", SpeedMultiplier.Create(0.03m));
        AddSpeedMenuItem(menu, "0.1倍", SpeedMultiplier.Create(0.1m));
        AddSpeedMenuItem(menu, "0.2倍", SpeedMultiplier.Create(0.2m));
        AddSpeedMenuItem(menu, "2倍", SpeedMultiplier.Create(2m));
        AddSpeedMenuItem(menu, "5倍", SpeedMultiplier.Create(5m));
        AddSpeedMenuItem(menu, "20倍", SpeedMultiplier.Create(20m));
        AddSpeedMenuItem(menu, "100倍", SpeedMultiplier.Create(100m));
        menu.Items.Add(new WpfSeparator());

        _recentSpeedItem = new WpfMenuItem();
        _recentSpeedItem.Click += async (_, _) =>
        {
            if (_speedHistory.Previous is { } previous)
            {
                await TryApplySpeedAsync(previous);
            }
        };
        menu.Items.Add(_recentSpeedItem);
        UpdateRecentSpeedItem();

        var custom = new WpfMenuItem { Header = "自定义…（0.01～100倍）" };
        custom.Click += async (_, _) =>
        {
            var initial = _flashHost.Current;
            var dialog = new CustomSpeedDialog(this, initial);
            if (dialog.ShowDialog() == true)
            {
                await TryApplySpeedAsync(dialog.SelectedMultiplier);
            }
        };
        menu.Items.Add(custom);
        UpdateSpeedChecks();
        return menu;
    }

    private void AddSpeedMenuItem(
        WpfContextMenu menu,
        string label,
        SpeedMultiplier multiplier)
    {
        var item = new WpfMenuItem
        {
            Header = label,
            IsCheckable = true,
        };
        item.Click += async (_, _) => await TryApplySpeedAsync(multiplier);
        _speedItems[multiplier.Value] = item;
        menu.Items.Add(item);
    }

    private async Task TryApplySpeedAsync(SpeedMultiplier multiplier)
    {
        try
        {
            var previous = await _flashHost.ApplySpeedAsync(multiplier);
            _speedHistory.RecordSuccessfulTransition(previous, multiplier);
            if (_speedHistory.Previous is { } recent)
            {
                _speedPreferenceStore.SaveRecent(recent);
            }

            UpdateSpeedChecks();
            UpdateRecentSpeedItem();
            if (_speedButton is not null)
            {
                _speedButton.ToolTip =
                    $"当前：{multiplier.DisplayText}；F3 与上一档倍率互换";
            }
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or InvalidOperationException
                or UnauthorizedAccessException
                or IOException
                or TimeoutException)
        {
            WpfMessageBox.Show(
                this,
                exception.Message,
                "变速不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateSpeedChecks()
    {
        foreach (var (value, item) in _speedItems)
        {
            item.IsChecked = value == _flashHost.Current.Value;
        }
    }

    private void UpdateRecentSpeedItem()
    {
        if (_recentSpeedItem is null)
        {
            return;
        }

        _recentSpeedItem.Header = _speedHistory.Previous is { } previous
            ? $"最近变速（{previous.DisplayText}，F3）"
            : "最近变速（暂无上一档，F3）";
        _recentSpeedItem.IsEnabled = _speedHistory.Previous is not null;
    }

    private async void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.F3)
        {
            return;
        }

        e.Handled = true;
        if (_speedHistory.Previous is { } previous)
        {
            await TryApplySpeedAsync(previous);
        }
    }

    private WpfContextMenu CreateScaleMenu()
    {
        var menu = new WpfContextMenu();
        AddScaleMenuItem(menu, "100%", 1m, GameWindowMode.Original);
        AddScaleMenuItem(menu, "150%", 1.5m, GameWindowMode.Scale150);
        AddScaleMenuItem(menu, "200%", 2m, GameWindowMode.Scale200);
        menu.Items.Add(new WpfSeparator());
        var fullScreen = new WpfMenuItem { Header = "全屏" };
        fullScreen.Click += async (_, _) =>
        {
            await ApplyFullScreenAsync();
            SaveWindowMode(GameWindowMode.FullScreen);
        };
        menu.Items.Add(fullScreen);
        UpdateScaleChecks();
        return menu;
    }

    private async Task ApplyWindowScaleAsync(decimal scale)
    {
        _isFullScreen = false;
        _windowScale = scale;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        var appliedScale = await FitWindowToGameScaleAsync(scale);
        _flashHost.SetScale(appliedScale);
        UpdateScaleChecks();
    }

    /// <summary>调整到请求档位；若屏幕限制尺寸，则回退到不裁切内容的最大等比倍率。</summary>
    private async Task<decimal> FitWindowToGameScaleAsync(decimal scale)
    {
        var targetWidth = checked((int)(NativeGameWidth * scale));
        var targetHeight = checked((int)(NativeGameHeight * scale));
        var actualSize = await FitClientAreaAsync(targetWidth, targetHeight);
        var reachedRequestedSize = Math.Abs(actualSize.Width - targetWidth) <= 1
            && Math.Abs(actualSize.Height - targetHeight) <= 1;
        var appliedScale = reachedRequestedSize
            ? scale
            : GameWindowSizing.CalculateContainedScale(
                actualSize.Width,
                actualSize.Height,
                NativeGameWidth,
                NativeGameHeight,
                scale);

        if (Math.Abs(appliedScale - scale) > 0.001m)
        {
            /* 高分辨率档位超过桌面上限时，缩窄另一边以保持游戏原生宽高比。 */
            targetWidth = Math.Max(1, (int)Math.Floor(NativeGameWidth * appliedScale));
            targetHeight = Math.Max(1, (int)Math.Floor(NativeGameHeight * appliedScale));
            actualSize = await FitClientAreaAsync(targetWidth, targetHeight);
            appliedScale = GameWindowSizing.CalculateContainedScale(
                actualSize.Width,
                actualSize.Height,
                NativeGameWidth,
                NativeGameHeight,
                scale);
        }

        CenterOnCurrentScreen();
        UpdateLayoutProbeTitle();
        return appliedScale;
    }

    /// <summary>迭代补偿 WPF 窗框与 DPI，返回最终实际客户区像素。</summary>
    private async Task<(int Width, int Height)> FitClientAreaAsync(
        int targetWidth,
        int targetHeight)
    {
        for (var attempt = 0; attempt < 4 && !_disposed; attempt++)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var clientWidth = _surface.ClientSize.Width;
            var clientHeight = _surface.ClientSize.Height;
            if (clientWidth <= 0 || clientHeight <= 0)
            {
                return (Math.Max(1, clientWidth), Math.Max(1, clientHeight));
            }

            if (Math.Abs(clientWidth - targetWidth) <= 1
                && Math.Abs(clientHeight - targetHeight) <= 1)
            {
                return (clientWidth, clientHeight);
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var newWidth = GameWindowSizing.AdjustWindowDimension(
                ActualWidth,
                clientWidth,
                targetWidth,
                dpi.DpiScaleX);
            var newHeight = GameWindowSizing.AdjustWindowDimension(
                ActualHeight,
                clientHeight,
                targetHeight,
                dpi.DpiScaleY);
            Width = Math.Max(MinWidth, newWidth);
            Height = Math.Max(MinHeight, newHeight);
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        return (
            Math.Max(1, _surface.ClientSize.Width),
            Math.Max(1, _surface.ClientSize.Height));
    }

    private async Task ApplyFullScreenAsync()
    {
        _isFullScreen = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (_surface.ClientSize.Width <= 0 || _surface.ClientSize.Height <= 0)
        {
            return;
        }

        _windowScale = Math.Min(
            (decimal)_surface.ClientSize.Width / NativeGameWidth,
            (decimal)_surface.ClientSize.Height / NativeGameHeight);
        _flashHost.SetScale(_windowScale);
        UpdateScaleChecks();
        UpdateLayoutProbeTitle();
    }

    private void AddScaleMenuItem(
        WpfContextMenu menu,
        string label,
        decimal scale,
        GameWindowMode windowMode)
    {
        var item = new WpfMenuItem
        {
            Header = label,
            IsCheckable = true,
        };
        item.Click += async (_, _) =>
        {
            await ApplyWindowScaleAsync(scale);
            SaveWindowMode(windowMode);
        };
        _scaleItems[scale] = item;
        menu.Items.Add(item);
    }

    private void ToggleMuted()
    {
        var isMuted = !_runtimePreferences.IsMuted;
        _flashHost.SetMuted(isMuted);
        _runtimePreferences = _runtimePreferences with { IsMuted = isMuted };
        UpdateMuteButton();
        try
        {
            _runtimePreferenceStore.SetMuted(isMuted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowPreferenceSaveWarning(exception);
        }
    }

    private void UpdateMuteButton()
    {
        if (_muteButton is not null)
        {
            _muteButton.Content = _runtimePreferences.IsMuted ? "取消静音" : "游戏静音";
        }
    }

    private void SaveWindowMode(GameWindowMode windowMode)
    {
        _runtimePreferences = _runtimePreferences with { WindowMode = windowMode };
        try
        {
            _runtimePreferenceStore.SetWindowMode(windowMode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowPreferenceSaveWarning(exception);
        }
    }

    private void ShowPreferenceSaveWarning(Exception exception) => WpfMessageBox.Show(
        this,
        $"当前设置已经生效，但无法保存到本地：{exception.Message}",
        "设置未保存",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    private void UpdateScaleChecks()
    {
        foreach (var (scale, item) in _scaleItems)
        {
            item.IsChecked = !_isFullScreen && scale == _windowScale;
        }
    }

    private void CenterOnCurrentScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var workArea = WinFormsScreen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var position = GameWindowPlacement.CenterInWorkArea(
            workArea.Left,
            workArea.Top,
            workArea.Width,
            workArea.Height,
            ActualWidth,
            ActualHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = position.Left;
        Top = position.Top;
    }

    private async Task ActivatePanelAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var panel = Process.GetProcessById(_panelProcessId);
                if (panel.HasExited)
                {
                    return;
                }

                panel.Refresh();
                if (panel.MainWindowHandle != IntPtr.Zero)
                {
                    _ = ShowWindowAsync(panel.MainWindowHandle, 9);
                    _ = SetForegroundWindow(panel.MainWindowHandle);
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static WpfButton CreateButton(string text) => new()
    {
        Content = text,
        Width = 104,
        Height = 30,
        Margin = new Thickness(4, 6, 4, 6),
        Padding = new Thickness(12, 2, 12, 2),
        Background = new SolidColorBrush(WpfColor.FromRgb(55, 65, 82)),
        Foreground = WpfBrushes.White,
        BorderThickness = new Thickness(0),
    };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

}

/// <summary>
/// 描述游戏窗口首次显示前需要应用的几何状态；纯计算 seam 用于锁定首帧尺寸回归。
/// </summary>
internal readonly record struct GameWindowStartupLayout(
    bool IsFullScreen,
    decimal RequestedScale,
    decimal AppliedScale,
    double Width,
    double Height)
{
    public static GameWindowStartupLayout Calculate(
        GameWindowMode windowMode,
        double originalWindowWidth,
        double originalWindowHeight,
        int workAreaWidth,
        int workAreaHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(originalWindowWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(originalWindowHeight, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(workAreaWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workAreaHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScaleX, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScaleY, 0);

        if (windowMode == GameWindowMode.FullScreen)
        {
            return new(true, 1m, 1m, originalWindowWidth, originalWindowHeight);
        }

        var requestedScale = windowMode switch
        {
            GameWindowMode.Scale150 => 1.5m,
            GameWindowMode.Scale200 => 2m,
            _ => 1m,
        };

        /*
         * WindowWidth/Height 是 96 DPI 下的 100% 外框基线。先剥离游戏区得到工具栏与窗框开销，
         * 再用物理工作区反推可容纳倍率，使首次 Show() 前就得到最终尺寸。
         */
        var chromeWidthDip = Math.Max(0, originalWindowWidth - FlashHostWindow.NativeGameWidth);
        var chromeHeightDip = Math.Max(0, originalWindowHeight - FlashHostWindow.NativeGameHeight);
        var availableGameWidth = Math.Max(1d, workAreaWidth - (chromeWidthDip * dpiScaleX));
        var availableGameHeight = Math.Max(1d, workAreaHeight - (chromeHeightDip * dpiScaleY));
        var containedScale = Math.Min(
            (double)requestedScale,
            Math.Min(
                availableGameWidth / FlashHostWindow.NativeGameWidth,
                availableGameHeight / FlashHostWindow.NativeGameHeight));
        var appliedScale = Math.Max(0.01m, (decimal)containedScale);
        var width = ((double)appliedScale * FlashHostWindow.NativeGameWidth / dpiScaleX)
            + chromeWidthDip;
        var height = ((double)appliedScale * FlashHostWindow.NativeGameHeight / dpiScaleY)
            + chromeHeightDip;

        return new(false, requestedScale, appliedScale, width, height);
    }
}
