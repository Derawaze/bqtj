namespace BqtjLauncher.Runtime.Flash;

public static class GameWindowSizing
{
    /// <summary>计算当前客户区在请求倍率内能够完整容纳的最大等比倍率。</summary>
    public static decimal CalculateContainedScale(
        int clientWidth,
        int clientHeight,
        int nativeWidth,
        int nativeHeight,
        decimal requestedScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clientWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(clientHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nativeWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nativeHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestedScale, 0);

        return Math.Min(
            requestedScale,
            Math.Min(
                (decimal)clientWidth / nativeWidth,
                (decimal)clientHeight / nativeHeight));
    }

    public static double AdjustWindowDimension(
        double currentWindowDip,
        int currentClientPixels,
        int targetClientPixels,
        double dpiScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(currentWindowDip, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentClientPixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetClientPixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScale, 0);

        return currentWindowDip + ((targetClientPixels - currentClientPixels) / dpiScale);
    }
}
