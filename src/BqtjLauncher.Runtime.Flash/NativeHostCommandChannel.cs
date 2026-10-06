using System.Globalization;
using System.IO;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>原生宿主的带编号命令通道；调用方负责串行执行需要回执的操作。</summary>
internal sealed class NativeHostCommandChannel : IDisposable
{
    private readonly TextWriter _input;
    private readonly TextReader _output;
    private readonly Action<string>? _observe;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _shutdown;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingGate = new();
    private PendingRequest? _pending;
    private IOException? _terminalError;
    private uint _requestId;
    private bool _disposed;

    public NativeHostCommandChannel(TextWriter input, TextReader output,
        Action<string>? observe = null, TimeSpan? timeout = null)
    {
        _input = input;
        _output = output;
        _observe = observe;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _shutdown = _lifetime.Token;
        // 每个宿主只有一个读取任务；请求超时只结束等待，不遗留多个流读取者。
        Completion = Task.Run(ReadRepliesAsync);
    }

    internal Task Completion { get; }

    /// <summary>无回执命令沿用原协议，凭据只写输入管道。</summary>
    public void Send(string command)
    {
        _writeGate.Wait(_shutdown);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _input.WriteLine(command);
            _input.Flush();
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>超时与取消只作用于本次请求；读取任务按编号丢弃它迟到的回执。</summary>
    public async Task<string> RequestAsync(string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PendingRequest pending;
        lock (_pendingGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_terminalError is not null) throw _terminalError;
            if (_pending is not null) throw new InvalidOperationException("宿主请求必须串行执行。");
            pending = new PendingRequest(++_requestId);
            _pending = pending;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown);
        linked.CancelAfter(_timeout);
        try
        {
            await _writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var line = $"request {pending.Id.ToString(CultureInfo.InvariantCulture)} {command}";
                await _input.WriteLineAsync(line.AsMemory(), linked.Token).ConfigureAwait(false);
                await _input.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
            return await pending.Reply.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(NativeHostCommandChannel));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("原生宿主响应超时，可再次刷新游戏；持续无响应请在管理面板重启该账号。");
        }
        finally
        {
            lock (_pendingGate)
                if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }

    /// <summary>持续读取并匹配当前编号；旧请求回执和普通进度行不会完成新请求。</summary>
    private async Task ReadRepliesAsync()
    {
        try
        {
            while (await _output.ReadLineAsync(_shutdown).ConfigureAwait(false) is { } line)
            {
                _observe?.Invoke(line);
                if (!line.StartsWith("reply ", StringComparison.Ordinal)) continue;
                var separator = line.IndexOf(' ', 6);
                if (separator < 0 || !uint.TryParse(line.AsSpan(6, separator - 6),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                lock (_pendingGate)
                    if (_pending?.Id == id) _pending.Reply.TrySetResult(line[(separator + 1)..]);
            }
            FailConnection(new IOException("原生宿主连接已关闭。请在管理面板重启该账号。"));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            FailConnection(new IOException("原生宿主连接已断开。请在管理面板重启该账号。", exception));
        }
    }

    /// <summary>宿主断开时结束当前请求，后续请求直接报告连接不可用。</summary>
    private void FailConnection(IOException error)
    {
        lock (_pendingGate)
        {
            if (_disposed) return;
            _terminalError = error;
            _pending?.Reply.TrySetException(error);
        }
    }

    /// <summary>窗口关闭立即结束当前请求；实际流由进程控制器关闭，避免争用其所有权。</summary>
    public void Dispose()
    {
        lock (_pendingGate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Reply.TrySetException(new ObjectDisposedException(nameof(NativeHostCommandChannel)));
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed class PendingRequest(uint id)
    {
        public uint Id { get; } = id;
        public TaskCompletionSource<string> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
