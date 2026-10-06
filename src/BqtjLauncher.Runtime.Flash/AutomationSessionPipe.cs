using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using BqtjLauncher.Application;
using Microsoft.Win32.SafeHandles;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>本机当前用户专属的限长请求协议；同时核对面板/容器PID，不传递平台凭据。</summary>
internal static class AutomationSessionPipe
{
    internal sealed record Request(Guid SessionId, string Command, decimal? Speed = null);
    internal sealed record Response(Guid SessionId, AutomationWindowTarget? Target = null,
        decimal? PreviousSpeed = null, string? Error = null);
    internal static string Name(Guid sessionId) => "bqtj-automation-" + sessionId.ToString("N");

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > 4096) throw new InvalidDataException("自动化消息过长。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > 4096) throw new InvalidDataException("自动化消息长度无效。");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("自动化消息为空。");
    }

    internal static async Task<Response> SendAsync(int containerPid, Request request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", Name(request.SessionId), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != containerPid)
            throw new InvalidDataException("自动化管道不属于本会话容器。");
        await WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        var response = await ReadAsync<Response>(pipe, timeout.Token).ConfigureAwait(false);
        if (response.SessionId != request.SessionId || response.Error is not null)
            throw new InvalidOperationException("本会话自动化目标尚不可用或请求被拒绝。");
        return response;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}

/// <summary>自动化容器的请求服务；关闭时取消等待并断开连接，不同步等待WPF调度器。</summary>
internal sealed class AutomationSessionServer : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeServerStream? _active;
    private readonly Task _loop;
    private int _disposed;
    internal Task Completion => _loop;

    internal AutomationSessionServer(Guid sessionId, int panelPid,
        Func<AutomationSessionPipe.Request, CancellationToken, Task<AutomationSessionPipe.Response>> handle)
    {
        _loop = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(AutomationSessionPipe.Name(sessionId), PipeDirection.InOut,
                    1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _active = pipe;
                try
                {
                    await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                    if (!AutomationSessionPipe.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != panelPid)
                        continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = await AutomationSessionPipe.ReadAsync<AutomationSessionPipe.Request>(pipe, timeout.Token).ConfigureAwait(false);
                    if (request.SessionId != sessionId) continue;
                    AutomationSessionPipe.Response response;
                    try { response = await handle(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { response = new(sessionId, Error: "unavailable"); }
                    await AutomationSessionPipe.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or ObjectDisposedException)
                { /* 客户端退出、限长拒绝或请求超时只结束连接，不终止用户游戏。 */ }
                finally { _active = null; }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _active?.Dispose();
        // 循环取消后才释放令牌源；不在窗口退出链同步等待调度器。
        _ = _loop.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
    }
}
