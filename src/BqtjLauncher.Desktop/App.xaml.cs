using System.Globalization;
using System.IO;
using System.Windows;
using BqtjLauncher.Application;
using BqtjLauncher.Infrastructure;
using BqtjLauncher.Runtime.Flash;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BqtjLauncher.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BqtjLauncher");
            Directory.CreateDirectory(localData);
            Directory.CreateDirectory(Path.Combine(localData, "logs"));

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(localData, "logs", "launcher-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger();

            if (FlashHostLaunchRequest.TryParse(e.Args, out var hostRequest))
            {
                var flash = FlashRuntimeDetector.Detect32Bit();
                if (!flash.IsAvailable)
                {
                    throw new InvalidOperationException(flash.Message);
                }

                // 容器仅按启动时绑定的账号 ID 读取自身凭据，不依赖面板当前选中项。
                var credentialStore = new SqliteGameProfileRepository(Path.Combine(localData, "launcher.db"));
                AccountCredential? credential = null;
                try { credential = await credentialStore.ReadCredentialAsync(hostRequest!.AccountId); }
                catch (Exception) { Log.Warning("未能读取本地账号信息，本次保留手动登录"); }
                var hostWindow = new FlashHostWindow(
                    hostRequest!.AccountId,
                    hostRequest.AccountName,
                    new FlashRuntimeOptions
                    {
                        GamePageUri = hostRequest.GamePageUri,
                    },
                    hostRequest.PanelProcessId,
                    hostRequest.LayoutProbeEnabled,
                    hostRequest.IsolationCompatibilityAudioDisabled,
                    credential,
                    () => credentialStore.ReadCredentialAsync(hostRequest.AccountId));
                MainWindow = hostWindow;
                // 先透明完成真实客户区校准和 Flash 启动，避免尺寸跳变、白边及初始化蓝帧。
                hostWindow.Opacity = 0;
                hostWindow.ShowActivated = false;
                hostWindow.Show();
                await hostWindow.StartAsync();
                return;
            }

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddSerilog(dispose: false));
            var repository = new SqliteGameProfileRepository(Path.Combine(localData, "launcher.db"));
            services.AddSingleton<IGameProfileRepository>(repository);
            services.AddSingleton<IAccountEditor>(repository);
            // 游戏由平台按版本发布包装页，入口地址只能运行时解析；容器固定地址仅作离线兜底。
            services.AddSingleton<IGamePageSource, HttpGamePageSource>();
            services.AddSingleton<IGameRuntime>(provider => new FlashGameRuntime(
                new FlashRuntimeOptions { GamePageUri = GamePageDefaults.PinnedGamePageUri },
                provider.GetRequiredService<IGamePageSource>()));
            services.AddSingleton<LauncherModule>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();

            var launcher = _services.GetRequiredService<LauncherModule>();
            await launcher.InitializeAsync();

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "启动器初始化失败");
            MessageBox.Show(
                $"启动器初始化失败：{exception.Message}",
                "爆枪突击启动器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
