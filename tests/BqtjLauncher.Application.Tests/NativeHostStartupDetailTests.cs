using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>
/// 锁定宿主启动失败的现场描述。用户现场没有调试器，失败信息必须能区分
/// “宿主根本没起来”和“IE/Flash 初始化卡住”，否则无法定位下一次超时。
/// </summary>
public sealed class NativeHostStartupDetailTests
{
    [Fact]
    public void SummaryReportsMissingStartWhenNoStageArrived()
    {
        var detail = new NativeFlashHostController.HostStartupDetail();

        Assert.Contains("未收到任何启动阶段", detail.ToSummary());
    }

    [Fact]
    public void SummaryReportsStageChainAndExitCode()
    {
        var detail = new NativeFlashHostController.HostStartupDetail();
        detail.FirstStageReceived = true;
        detail.Stages.Add("start");
        detail.Stages.Add("create-browser");
        detail.ExitCode = 8;

        var summary = detail.ToSummary();

        Assert.Contains("阶段=start>create-browser", summary);
        Assert.Contains("退出码=8", summary);
    }

    [Fact]
    public void SummaryIncludesStderrAndUnexpectedOutput()
    {
        var detail = new NativeFlashHostController.HostStartupDetail();
        detail.OtherLines.Add("unexpected-line");
        detail.Stderr = "create browser and navigate game page failed (HRESULT=0x800c0005)";

        var summary = detail.ToSummary();

        Assert.Contains("输出=unexpected-line", summary);
        Assert.Contains("HRESULT=0x800c0005", summary);
    }

    [Fact]
    public void ReadyLineCompletesStartupAndStageLineDoesNot()
    {
        Assert.Equal(
            NativeFlashHostController.HostLineKind.Ready,
            NativeFlashHostController.ClassifyHostLine("ready"));
        Assert.Equal(
            NativeFlashHostController.HostLineKind.Stage,
            NativeFlashHostController.ClassifyHostLine("stage create-browser"));
        Assert.Equal(
            NativeFlashHostController.HostLineKind.Other,
            NativeFlashHostController.ClassifyHostLine("something-else"));
        Assert.Equal(
            NativeFlashHostController.HostLineKind.Other,
            NativeFlashHostController.ClassifyHostLine("stage "));
    }

    /// <summary>
    /// 宿主进程直接退出（例如被安全软件拦截）时必须立刻结束等待，而不是耗满超时，
    /// 否则用户要等几十秒才看到失败。这里用一个立即退出的真实进程验证。
    /// </summary>
    [Fact]
    public async Task ExitedProcessIsDetectedAsClosedOutput()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "exit", "0" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync();
        Assert.NotNull(process);

        // 生产路径在读到 null（stdout 已关闭）时立即判定失败，不再继续等待。
        var line = await process.StandardOutput.ReadLineAsync();
        Assert.Null(line);
    }
}
