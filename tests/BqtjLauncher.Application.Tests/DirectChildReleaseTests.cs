using System.Drawing;
using System.Windows.Forms;
using MaaBackgroundProbe;

namespace BqtjLauncher.Application.Tests;

/// <summary>仅创建不显示的合成控件，验证按下后停止仍释放；不依赖桌面光标或真实游戏。</summary>
public sealed class DirectChildReleaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasesOwnedTargetEvenWhenNormalInputBecomesUnavailable(bool cancelAfterDown)
    {
        var ready = new TaskCompletionSource<ClickPanel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unavailable = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var panel = new ClickPanel(() => { if (cancelAfterDown) Volatile.Write(ref unavailable, 1); });
                _ = panel.Handle;
                ready.SetResult(panel);
                System.Windows.Forms.Application.Run();
                finished.SetResult();
            }
            catch (Exception exception) { ready.TrySetException(exception); finished.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var target = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var handle = target.Handle;
        var releases = 0;
        try
        {
            var click = DirectChildInput.ClickWindowAsync(handle, new Point(40, 40),
                () => Volatile.Read(ref unavailable) == 0 ? new Size(200, 160) : throw new OperationCanceledException(),
                () => { if (Volatile.Read(ref unavailable) != 0) throw new OperationCanceledException(); },
                () => Interlocked.Increment(ref releases));
            if (cancelAfterDown) await Assert.ThrowsAsync<OperationCanceledException>(() => click);
            else await click;
            Assert.Equal(1, target.Downs);
            Assert.Equal(1, target.Ups);
            Assert.Equal(1, releases);
        }
        finally
        {
            target.BeginInvoke(() => System.Windows.Forms.Application.ExitThread());
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ClickPanel(Action onDown) : Panel
    {
        public int Downs;
        public int Ups;
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x201) { Interlocked.Increment(ref Downs); onDown(); message.Result = IntPtr.Zero; return; }
            if (message.Msg == 0x202) { Interlocked.Increment(ref Ups); message.Result = IntPtr.Zero; return; }
            base.WndProc(ref message);
        }
    }
}
