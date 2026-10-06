using System.Windows;
using System.Windows.Controls;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Desktop;

/// <summary>首版单账号日常入口，固定打开时的账号；停止只取消外层操作，不强关游戏。</summary>
public sealed class DailyScriptWindow : Window
{
    private readonly Guid _profileId;
    private readonly DailyScriptRunner _runner;
    private readonly MaaDailyScriptExecutor _executor;
    private readonly Button _start = new() { Content = "启动日常", Margin = new Thickness(4), MinWidth = 110 };
    private readonly Button _stop = new() { Content = "停止", Margin = new Thickness(4), MinWidth = 80, IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 16, 4, 0) };
    private readonly ComboBox _speed = new() { ItemsSource = new[] { 1, 5, 10, 20, 50 }, SelectedIndex = 0, Width = 90 };
    private readonly Grid _saves = new();
    private int _saveNumber = 1;
    private CancellationTokenSource? _run;
    private bool _closed;

    public DailyScriptWindow(Guid profileId, string accountName, DailyScriptRunner runner, MaaDailyScriptExecutor executor)
    {
        _profileId = profileId;
        _runner = runner;
        _executor = executor;
        Title = "日常脚本 · " + accountName;
        Background = (System.Windows.Media.Brush)FindResource("WindowBackground");
        Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        Width = 470; Height = 410; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(20) };
        body.Children.Add(new TextBlock { Text = "选择存档（从上到下、每行从左到右编号）", Margin = new Thickness(4, 0, 4, 12) });
        for (var row = 0; row < 4; row++) _saves.RowDefinitions.Add(new RowDefinition());
        for (var column = 0; column < 2; column++) _saves.ColumnDefinitions.Add(new ColumnDefinition());
        for (var number = 1; number <= 8; number++)
        {
            var slot = number;
            var choice = new RadioButton
            {
                Content = "存档 " + number,
                GroupName = "Save",
                IsChecked = number == 1,
                Margin = new Thickness(8),
                Padding = new Thickness(4)
            };
            choice.Checked += (_, _) => _saveNumber = slot;
            Grid.SetRow(choice, (number - 1) / 2); Grid.SetColumn(choice, (number - 1) % 2);
            _saves.Children.Add(choice);
        }
        body.Children.Add(_saves);
        var speedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 10, 4, 10) };
        speedRow.Children.Add(new TextBlock { Text = "运行倍率：", VerticalAlignment = VerticalAlignment.Center });
        speedRow.Children.Add(_speed); body.Children.Add(speedRow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_start); buttons.Children.Add(_stop); body.Children.Add(buttons);
        body.Children.Add(_status); Content = body;
        _status.Text = "已有游戏窗口会跳过。最终保存确认后关闭本次创建的窗口；取消或失败保留窗口。";
        _start.Click += Start_Click;
        _stop.Click += (_, _) => _run?.Cancel();
        _executor.StageChanged += StageChanged;
        Closed += (_, _) => { _closed = true; _executor.StageChanged -= StageChanged; _run?.Cancel(); };
    }

    private void StageChanged(string stage)
    {
        if (!_closed && _run is not null) Dispatcher.BeginInvoke(() => { if (!_closed) _status.Text = stage + "…"; });
    }

    /// <summary>先检查组件再启动；固定本次存档和倍率，不随界面后续变化而改动。</summary>
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_run is not null) return;
        try { _executor.EnsureAvailable(); }
        catch (InvalidOperationException exception) { _status.Text = exception.Message; return; }
        var save = _saveNumber;
        var speed = SpeedMultiplier.Create((int)_speed.SelectedItem);
        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        _start.IsEnabled = _saves.IsEnabled = _speed.IsEnabled = false;
        _stop.IsEnabled = true;
        _status.Text = "正在启动所选账号…";
        try
        {
            var result = await _runner.RunAsync(_profileId, save, speed, cancellation.Token);
            if (_closed) return;
            _status.Text = result.Status switch
            {
                DailyScriptRunStatus.Completed => "本次最终保存已确认，账号窗口已关闭。" + (result.HasFailedItems ? "部分中间任务未完成。" : ""),
                DailyScriptRunStatus.Skipped => "该账号已有游戏窗口，已跳过。请先自行关闭，再启动日常。",
                DailyScriptRunStatus.Cancelled => "已停止外层操作，保留游戏窗口；游戏内置脚本可能仍在运行。",
                _ => (result.Detail ?? "未确认保存完成。") + "已保留游戏窗口，请检查当前画面。",
            };
            if (result.NeedsManualSpeedRestore) _status.Text += " 倍率未恢复，请手动调整。";
            if (result.WorkerCleanupFailed) _status.Text += " 视觉组件收尾异常，请暂停重试。";
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "已取消。"; }
        catch (Exception) { if (!_closed) _status.Text = "日常任务未完成，已停止外层操作；请检查游戏窗口。"; }
        finally
        {
            _run = null;
            if (!_closed)
            {
                _start.IsEnabled = _saves.IsEnabled = _speed.IsEnabled = true;
                _stop.IsEnabled = false;
            }
        }
    }
}
