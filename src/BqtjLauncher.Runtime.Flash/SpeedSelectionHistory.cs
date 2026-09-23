using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>记录最后一次成功切换前的倍率，使菜单和 F3 能在当前档与上一档之间反复互换。</summary>
public sealed class SpeedSelectionHistory
{
    public SpeedSelectionHistory(SpeedMultiplier? previous)
    {
        Previous = previous;
    }

    public SpeedMultiplier? Previous { get; private set; }

    /// <summary>仅在倍率实际变化且应用成功后，把切换前的档位记为上一档。</summary>
    public void RecordSuccessfulTransition(
        SpeedMultiplier current,
        SpeedMultiplier target)
    {
        if (current == target)
        {
            return;
        }

        Previous = current;
    }
}
