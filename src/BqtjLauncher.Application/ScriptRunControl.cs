namespace BqtjLauncher.Application;

/// <summary>根运行的协作暂停闸门；保留调用栈和当前语句，不取消已经发出的游戏动作。</summary>
public sealed class ScriptRunControl
{
    private readonly object _sync = new();
    private TaskCompletionSource? _resume;
    private long _revision;
    public bool IsPaused { get { lock (_sync) return _resume is not null; } }
    public long Revision { get { lock (_sync) return _revision; } }

    public void Pause()
    {
        lock (_sync)
        {
            if (_resume is not null) return;
            _revision++;
            _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_sync) { var waiting = _resume; _resume = null; waiting?.TrySetResult(); }
    }

    /// <summary>停止令牌可以打断暂停等待；返回实际暂停时间，供视觉超时扣除。</summary>
    public async Task<TimeSpan> WaitAsync(CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var waited = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task? task;
            lock (_sync) task = _resume?.Task;
            if (task is null) return waited ? System.Diagnostics.Stopwatch.GetElapsedTime(started) : TimeSpan.Zero;
            waited = true;
            await task.WaitAsync(token).ConfigureAwait(false);
        }
    }
}
