using System.Runtime.InteropServices;
using System.Text.Json;

namespace MaaBackgroundProbe;

/// <summary>以嵌套子窗口回归输入路由：父窗口和遮挡窗口不得收到子窗口的点击。</summary>
internal static class DirectChildRegression
{
    public static int Run(string output)
    {
        using var first = new ProbeWindow("Synthetic parent A", Color.Red, Color.Yellow);
        using var second = new ProbeWindow("Synthetic parent B", Color.Blue, Color.Lime);
        using var cover = new ProbeWindow("Synthetic child routing cover", Color.Gray, Color.Gray);
        using var childA = new ClickChild { BackColor = Color.Red };
        using var childB = new ClickChild { BackColor = Color.Blue };
        first.Controls.Add(childA);
        second.Controls.Add(childB);
        first.Location = second.Location = cover.Location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40);
        var exitCode = 1;
        cover.Shown += async (_, _) =>
        {
            string? error = null;
            try
            {
                await Task.Delay(100);
                var foreground = GetForegroundWindow();
                if (!GetCursorPos(out var cursor)) throw new InvalidOperationException("无法读取光标。");
                void AssertUndisturbed()
                {
                    if (GetForegroundWindow() != foreground || !GetCursorPos(out var current) || current != cursor)
                        throw new InvalidOperationException("输入改变了前台焦点或系统光标。");
                }
                // 在UI线程取句柄和尺寸；工作线程只使用快照，避免跨线程创建或访问控件。
                var handleA = childA.Handle;
                var handleB = childB.Handle;
                var sizeA = childA.ClientSize;
                var sizeB = childB.ClientSize;
                if (WindowFromPoint(childA.PointToScreen(new Point(40, 40))) != cover.Handle)
                    throw new InvalidOperationException("子窗口未被遮挡，测试条件不成立。");
                await DirectChildInput.ClickWindowAsync(handleA, new Point(40, 40), () => sizeA, AssertUndisturbed);
                if (childA.Downs != 1 || childA.Ups != 1 || childB.Downs != 0)
                    throw new InvalidOperationException("A子窗口点击未完整送达或串到B。");
                await DirectChildInput.ClickWindowAsync(handleB, new Point(40, 40), () => sizeB, AssertUndisturbed);
                if (childB.Downs != 1 || childB.Ups != 1 || childA.Downs != 1)
                    throw new InvalidOperationException("B子窗口点击未完整送达或串到A。");
                // 同时发送到不同子窗口，模拟多个账号并行；目标仍各自绑定，不能共享输入句柄。
                await Task.WhenAll(
                    DirectChildInput.ClickWindowAsync(handleA, new Point(40, 40), () => sizeA, AssertUndisturbed),
                    DirectChildInput.ClickWindowAsync(handleB, new Point(40, 40), () => sizeB, AssertUndisturbed));
                if (childA.Downs != 2 || childA.Ups != 2 || childB.Downs != 2 || childB.Ups != 2)
                    throw new InvalidOperationException("并行输入未完整送达各自子窗口。");
                // 模拟按下后取消/隐藏：后续正常输入拒绝，但finally仍向原子窗口释放。
                var stopped = 0;
                childA.DownObserved = () => Volatile.Write(ref stopped, 1);
                var cancelled = false;
                try
                {
                    await DirectChildInput.ClickWindowAsync(handleA, new Point(40, 40),
                        () => Volatile.Read(ref stopped) == 0 ? sizeA : throw new OperationCanceledException(),
                        () => { AssertUndisturbed(); if (Volatile.Read(ref stopped) != 0) throw new OperationCanceledException(); },
                        () => { if (!IsWindow(handleA)) throw new InvalidOperationException(); });
                }
                catch (OperationCanceledException) { cancelled = true; }
                if (!cancelled || childA.Downs != 3 || childA.Ups != 3 || childB.Downs != 2)
                    throw new InvalidOperationException("按下后取消未释放或串到其他目标。");
                if (first.Clicks != 0 || second.Clicks != 0 || cover.Clicks != 0)
                    throw new InvalidOperationException("点击串到了顶层容器或遮挡窗口。");
                AssertUndisturbed();
                exitCode = 0;
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                var report = new
                {
                    passed = exitCode == 0,
                    input = "DirectChild",
                    firstDowns = childA.Downs,
                    firstUps = childA.Ups,
                    secondDowns = childB.Downs,
                    secondUps = childB.Ups,
                    parentClicks = first.Clicks + second.Clicks,
                    coverClicks = cover.Clicks,
                    error,
                    scope = "Synthetic nested child windows only; not a full Flash script test.",
                };
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report));
                cover.Close();
            }
        };
        first.Show();
        second.Show();
        cover.TopMost = true;
        Application.Run(cover);
        return exitCode;
    }

    /// <summary>只接受指定位置的合成按下和释放，不自动捕获鼠标或激活父窗口。</summary>
    private sealed class ClickChild : Panel
    {
        public int Downs { get; private set; }
        public int Ups { get; private set; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Action? DownObserved { get; set; }
        public ClickChild() { Dock = DockStyle.Fill; }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg is 0x201 or 0x202)
            {
                var value = message.LParam.ToInt64();
                if ((short)value == 40 && (short)(value >> 16) == 40)
                {
                    if (message.Msg == 0x201) { Downs++; DownObserved?.Invoke(); }
                    else Ups++;
                }
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
}
