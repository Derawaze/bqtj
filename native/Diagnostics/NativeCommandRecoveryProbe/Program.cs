using System.Diagnostics;
using BqtjLauncher.Domain;
using BqtjLauncher.Runtime.Flash;

// 实际匿名进程管道连接生产读取模块与原生命令线程；所有输入均为虚构控制命令。
if (args.Length != 1) throw new ArgumentException("需要 CommandRecoveryHostProbe.exe 路径。");
using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[0]))
{
    UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardInput = true, RedirectStandardOutput = true,
}) ?? throw new InvalidOperationException("无法启动虚构原生宿主。");
NativeHostCommandChannel? commands = null;
try
{
    Require(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "ready", "启动");
    var legacyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var showStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    commands = new NativeHostCommandChannel(process.StandardInput, process.StandardOutput,
        line =>
        {
            if (line == "show-ok") legacyReply.TrySetResult();
            if (line == "stage probe-show-received") showStarted.TrySetResult();
        }, TimeSpan.FromMilliseconds(800));

    try { await commands.RequestAsync("reload"); throw new InvalidOperationException("首次阻塞没有超时。"); }
    catch (TimeoutException) { Console.WriteLine("PASS: 首次请求超时"); }
    Require(await commands.RequestAsync("reload") == "reload-ok", "迟到回执后再次刷新");
    Require(await commands.RequestAsync("display-ready") == "display-pending", "编号就绪查询");

    using (var cancellation = new CancellationTokenSource())
    {
        var pending = commands.RequestAsync("show", cancellation.Token);
        await showStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        try { await pending; throw new InvalidOperationException("取消未生效。"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS: 请求取消"); }
    }
    Require(await commands.RequestAsync("show") == "show-ok", "取消后再次显示");
    // 此夹具未加载 Flash，实际速度分派应带原请求编号返回错误，而非裸回执。
    Require((await commands.RequestAsync("speed 2")).StartsWith("speed-error ", StringComparison.Ordinal), "编号速度错误回执");
    commands.Send("show");
    await legacyReply.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine("PASS: 旧命令兼容");

    var closing = commands.RequestAsync("synthetic-no-reply");
    commands.Dispose();
    try { await closing; throw new InvalidOperationException("关闭未结束请求。"); }
    catch (ObjectDisposedException) { Console.WriteLine("PASS: 关闭结束等待"); }
    process.StandardInput.Close();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    await commands.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Require(process.ExitCode == 0, "匿名管道读取任务与原生进程正常结束");
}
finally
{
    commands?.Dispose();
    if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); }
}

// 再经过实际控制器，验证启动移交、刷新尺寸恢复与排队请求在关闭时结束。
using var controller = new NativeFlashHostController(new Uri("about:blank"), audioControlEnabled: false);
var controllerShowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
controller.LineObserver = line =>
{
    if (line == "stage probe-show-received") controllerShowStarted.TrySetResult();
};
controller.Start(0, 950, 600);
await controller.ReloadAsync();
Console.WriteLine("PASS: 控制器启动与刷新尺寸恢复");
var sameSpeedRejected = false;
try
{
    // 虚构宿主无 Flash：同档请求必须实际确认，不能因旧缓存直接返回成功。
    await controller.ApplySpeedAsync(SpeedMultiplier.Original);
}
catch (InvalidOperationException) { sameSpeedRejected = true; }
Require(sameSpeedRejected, "同档变速重新确认");
var controllerShow = controller.ShowAsync();
await controllerShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
var queuedReload = controller.ReloadAsync();
controller.Dispose();
foreach (var pending in new[] { controllerShow, queuedReload })
{
    try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); throw new IOException("关闭后请求未结束。"); }
    catch (ObjectDisposedException) { }
}
Console.WriteLine("PASS: 控制器关闭结束当前与排队请求");

static void Require(bool condition, string scope)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + scope);
    Console.WriteLine("PASS: " + scope);
}
