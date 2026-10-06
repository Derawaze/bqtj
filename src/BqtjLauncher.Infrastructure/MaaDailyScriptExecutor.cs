using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BqtjLauncher.Application;

namespace BqtjLauncher.Infrastructure;

/// <summary>通过独立x64进程执行视觉操作；标准输入只传目标身份，不传账号密码。</summary>
public sealed class MaaDailyScriptExecutor(string automationDirectory) : IDailyScriptExecutor
{
    public event Action<string>? StageChanged;
    private string WorkerPath => Path.Combine(automationDirectory, "background-probe", "MaaBackgroundProbe.exe");

    /// <summary>启动账号前检查可选组件，缺失时不产生半途启动的游戏窗口。</summary>
    public void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("日常组件仅支持Windows。");
        if (!File.Exists(WorkerPath)
            || !File.Exists(Path.Combine(automationDirectory, "maa-5.14.2-win-x64", "bin", "MaaFramework.dll"))
            || !Directory.Exists(Path.Combine(automationDirectory, "ppocr-v4-zh-cn", "model", "ocr")))
            throw new InvalidOperationException("缺少日常脚本组件，请先准备开发组件。");
    }

    public async IAsyncEnumerable<DailyScriptFrame> ExecuteAsync(AutomationWindowTarget target, int saveNumber,
        Guid runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException();
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\bqtj-daily-stop-" + runId.ToString("N"));
        var info = new ProcessStartInfo(WorkerPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(WorkerPath)!,
        };
        info.ArgumentList.Add("--daily-worker");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动视觉组件。");
        // 先协作停止，给已按下的消息释放机会；第三方阻塞超过3秒才强制结束本组件。
        using var cancellation = cancellationToken.Register(() =>
        {
            stop.Set();
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => StopWorker(process), TaskScheduler.Default);
        });
        var errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Target = target, SaveNumber = saveNumber, RunId = runId,
                ParentProcessId = Environment.ProcessId, AutomationDirectory = Path.GetFullPath(automationDirectory),
            }).AsMemory(), cancellationToken);
            process.StandardInput.Close();
            while (true)
            {
                // 每条消息与原生操作均有限时；不接受进程退出或任意“success”作为保存证据。
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
                if (line is null) break;
                if (line.Length > 4096) throw new InvalidOperationException("视觉消息超长。");
                using var message = JsonDocument.Parse(line);
                var kind = message.RootElement.GetProperty("Kind").GetString();
                if (kind == "Frame")
                {
                    var frame = message.RootElement.GetProperty("Frame").Deserialize<DailyScriptFrame>()
                        ?? throw new InvalidOperationException("视觉消息为空。");
                    if (frame.SessionId != target.SessionId || frame.RunId != runId)
                        throw new InvalidOperationException("视觉消息身份不符。");
                    yield return frame;
                }
                else if (kind == "Stage")
                {
                    var stage = message.RootElement.GetProperty("Stage").GetString();
                    // 仅允许固定阶段名，原始OCR或异常不能经提示框输出。
                    if (DailyScriptExecutionException.IsKnownStage(stage)) StageChanged?.Invoke(stage!);
                }
                else if (kind == "Error")
                {
                    var root = message.RootElement;
                    var stage = root.TryGetProperty("Stage", out var phase) ? phase.GetString() : null;
                    var code = root.TryGetProperty("Code", out var fault) && fault.TryGetInt32(out var number)
                        ? (DailyScriptFailureCode)number : DailyScriptFailureCode.Initialization;
                    var width = root.TryGetProperty("Width", out var w) && w.TryGetInt32(out var ww) ? ww : 0;
                    var height = root.TryGetProperty("Height", out var h) && h.TryGetInt32(out var hh) ? hh : 0;
                    throw new DailyScriptExecutionException(stage, code, width, height);
                }
                else throw new InvalidOperationException("视觉组件未完成当前阶段。");
            }
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException("视觉组件停止。");
        }
        finally
        {
            stop.Set();
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (TimeoutException)
            {
                StopWorker(process);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            await errorDrain.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    /// <summary>仅终止本次直接创建的视觉进程，不按名称扫描或结束游戏。</summary>
    private static void StopWorker(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
    }
}
