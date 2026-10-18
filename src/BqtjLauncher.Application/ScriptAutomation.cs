using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>视觉和执行层共用的游戏坐标，避免应用层引用Windows绘图类型。</summary>
public readonly record struct ScriptRect(int X, int Y, int Width, int Height);
public sealed record ScriptText(string Text, ScriptRect Bounds, double Confidence);
public sealed record ScriptVisionFrame(long SnapshotId, long CapturedAt, IReadOnlyList<ScriptText> Text);
public sealed record ScriptPixels(int Width, int Height, byte[] Bgr);

/// <summary>指定文字的画面样式；坐标始终返回原始游戏坐标。</summary>
public enum ScriptTextStyle { Normal, Green, SaveName }

/// <summary>共享视觉组件按需租用；截图缓存只属于绑定目标和本次快照。</summary>
public interface IScriptVision
{
    Task BeginAsync(CancellationToken token);
    Task EndAsync();
    Task<ScriptVisionFrame> ReadAsync(AutomationWindowTarget target, ScriptRect region, CancellationToken token,
        long snapshotId = 0, ScriptTextStyle style = ScriptTextStyle.Normal);
    Task<ScriptPixels> PixelsAsync(AutomationWindowTarget target, ScriptRect region, long snapshotId, CancellationToken token);
    Task<ScriptRect?> FindImageAsync(AutomationWindowTarget target, ScriptRect region, byte[] image, CancellationToken token);
    Task ClickAsync(AutomationWindowTarget target, int x, int y, CancellationToken token);
    Task ReleaseTargetAsync(AutomationWindowTarget target);
}

/// <summary>视觉执行器只操作签发的会话；保存由官方脚本执行，外部不补点。</summary>
public interface IScriptExecutor
{
    Task BeginAsync(CancellationToken token);
    Task EndAsync();
    Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(AutomationGameSession session, Action<string> stage, CancellationToken token);
    Task ExecuteAsync(AutomationGameSession session, ScriptDefinition definition, ScriptPlanTask task,
        SpeedMultiplier speed, Action<string, ScriptRunState> stage, CancellationToken token);
    Task ExitSaveAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token);
    Task ReleaseAsync(AutomationGameSession session);
    /// <summary>接管已有游戏时只观察当前存档，不自动退出未知存档。</summary>
    Task<bool> IsInSaveAsync(AutomationGameSession session, CancellationToken token) => Task.FromResult(false);
}

/// <summary>目标不可见、最小化或尺寸变化时只暂停观察，不改用系统鼠标或重放已发出的点击。</summary>
public sealed class ScriptTargetUnavailableException(string message) : InvalidOperationException(message);
