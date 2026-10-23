using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>视觉和执行层共用的游戏坐标，避免应用层引用Windows绘图类型。</summary>
public readonly record struct ScriptRect(int X, int Y, int Width, int Height);
public sealed record ScriptText(string Text, ScriptRect Bounds, double Confidence);
public sealed record ScriptVisionFrame(long SnapshotId, long CapturedAt, IReadOnlyList<ScriptText> Text);
public sealed record ScriptPixels(int Width, int Height, byte[] Bgr);
/// <summary>不识字的同帧观察，保留截图编号和时间，供战后走位校验输入新鲜度。</summary>
public sealed record ScriptSceneFrame(ScriptVisionFrame Frame, ScriptPixels Pixels);

/// <summary>指定文字的画面样式；坐标始终返回原始游戏坐标。</summary>
public enum ScriptTextStyle { Normal, Green, SaveName, QuotaCounter }

/// <summary>普通模板匹配菜单图标；特征匹配用于校准旋转/缩放的战场物品。</summary>
public enum ScriptImageAlgorithm { Template, Features }

/// <summary>共享视觉组件按需租用；截图缓存只属于绑定目标和本次快照。</summary>
public interface IScriptVision
{
    bool SupportsBattleInput => false;
    bool SupportsDiagonalInput => false;
    bool SupportsBattleAi => false;
    bool SupportsBattleAiRise => false;
    /// <summary>协议7单独隔离蓝色/绿色计数，和材料名称使用同一截图。</summary>
    bool SupportsQuotaCounter => false;
    Task BeginAsync(CancellationToken token);
    Task EndAsync();
    Task<ScriptVisionFrame> ReadAsync(AutomationWindowTarget target, ScriptRect region, CancellationToken token,
        long snapshotId = 0, ScriptTextStyle style = ScriptTextStyle.Normal);
    Task<ScriptPixels> PixelsAsync(AutomationWindowTarget target, ScriptRect region, long snapshotId, CancellationToken token);
    /// <summary>生产组件直接截图取像素；默认适配保留其他视觉实现的兼容性。</summary>
    async Task<ScriptSceneFrame> CaptureAsync(AutomationWindowTarget target, CancellationToken token)
    {
        var full = new ScriptRect(0, 0, 950, 600);
        var frame = await ReadAsync(target, full, token).ConfigureAwait(false);
        return new(frame, await PixelsAsync(target, full, frame.SnapshotId, token).ConfigureAwait(false));
    }
    Task<ScriptRect?> FindImageAsync(AutomationWindowTarget target, ScriptRect region, byte[] image, CancellationToken token);
    Task ClickAsync(AutomationWindowTarget target, int x, int y, CancellationToken token);
    Task ReleaseTargetAsync(AutomationWindowTarget target);
    Task MovePointerAsync(AutomationWindowTarget target, int x, int y, CancellationToken token)
        => throw new NotSupportedException("视觉组件未提供后台悬停。");
    Task HoldKeyAsync(AutomationWindowTarget target, char key, int milliseconds, CancellationToken token)
        => throw new NotSupportedException("视觉组件未提供后台移动。");
    /// <summary>两个方向同时按下并统一释放，用于斜线移动。</summary>
    Task HoldKeysAsync(AutomationWindowTarget target, char first, char second, int milliseconds, CancellationToken token)
        => throw new NotSupportedException("视觉组件未提供斜线移动，请更新组件。");
    /// <summary>独立的N键短按，只用于已观察到状态的游戏AI切换，不开放任意键盘输入。</summary>
    Task ToggleBattleAiAsync(AutomationWindowTarget target, CancellationToken token)
        => throw new NotSupportedException("视觉组件未提供AI切换，请更新组件。");
    /// <summary>一次请求同时按N和W，N先释放，W继续完成首个上升短按，取消仍等待两键松开。</summary>
    Task ToggleBattleAiAndRiseAsync(AutomationWindowTarget target, int milliseconds, CancellationToken token)
        => throw new NotSupportedException("同步关闭AI并上升需要协议6脚本组件。");
    Task<IReadOnlyList<ScriptRect>> FindImagesAsync(AutomationWindowTarget target, ScriptRect region, byte[] image, long snapshotId, CancellationToken token,
        ScriptImageAlgorithm algorithm = ScriptImageAlgorithm.Template)
        => throw new NotSupportedException("视觉组件未提供同帧多目标识图。");
}

/// <summary>视觉执行器只操作签发的会话；保存由官方脚本执行，外部不补点。</summary>
public interface IScriptExecutor
{
    /// <summary>每次根运行独立持有暂停闸门，避免扫描或其他窗口共享可变的暂停状态。</summary>
    IScriptExecutor ForRun(ScriptRunControl control) => this;
    /// <summary>只有真实地图、战斗结束、掉落拾取和限额适配全部可用时才开放执行。</summary>
    bool SupportsDailyMaterialFarming => false;
    string DailyMaterialUnavailableReason => "天空顶刷材料尚待实机图像校准，当前不能自动执行；方案可以先保存。";
    Task BeginAsync(CancellationToken token);
    Task EndAsync();
    Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(AutomationGameSession session, Action<string> stage, CancellationToken token);
    Task ExecuteAsync(AutomationGameSession session, ScriptDefinition definition, ScriptPlanTask task,
        SpeedMultiplier speed, Action<string, ScriptRunState> stage, CancellationToken token);
    Task ExitSaveAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token);
    Task ReleaseAsync(AutomationGameSession session);
    /// <summary>后续语句复用日常窗口前收起系统面板；最终完成画面仍可保留。</summary>
    Task DismissSystemAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
        => Task.CompletedTask;
    /// <summary>日常语句自己的同档收尾：关闭保存系统页并收起官方侧栏，给后续操作交付可用的城镇。</summary>
    Task DismissDailyPanelsAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
        => DismissSystemAsync(session, stage, token);
    /// <summary>接管时只观察是否进入存档；指定档切换由持有互斥会话的调度器安排。</summary>
    Task<bool> IsInSaveAsync(AutomationGameSession session, CancellationToken token) => Task.FromResult(false);
    Task FarmDailyMaterialsAsync(AutomationGameSession session, DailyMaterialFarmOptions options,
        Action<string, ScriptRunState> stage, CancellationToken token)
        => throw new NotSupportedException("天空顶刷材料尚待实机图像校准，当前不能自动执行。");
}

/// <summary>目标不可见、最小化或尺寸变化时只暂停观察，不改用系统鼠标或重放已发出的点击。</summary>
public sealed class ScriptTargetUnavailableException(string message) : InvalidOperationException(message);
