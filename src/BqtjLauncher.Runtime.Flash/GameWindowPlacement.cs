namespace BqtjLauncher.Runtime.Flash;

public static class GameWindowPlacement
{
    public static (double Left, double Top) CenterInWorkArea(
        int workAreaLeftPixels,
        int workAreaTopPixels,
        int workAreaWidthPixels,
        int workAreaHeightPixels,
        double windowWidthDip,
        double windowHeightDip,
        double dpiScaleX,
        double dpiScaleY)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workAreaWidthPixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workAreaHeightPixels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowWidthDip, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowHeightDip, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScaleX, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dpiScaleY, 0);

        var leftPixels = workAreaLeftPixels
            + ((workAreaWidthPixels - (windowWidthDip * dpiScaleX)) / 2d);
        var topPixels = workAreaTopPixels
            + ((workAreaHeightPixels - (windowHeightDip * dpiScaleY)) / 2d);
        return (leftPixels / dpiScaleX, topPixels / dpiScaleY);
    }
}
