namespace BqtjLauncher.Application;

/// <summary>启动器正式版信息；开发版本仅展示正式版，不误报为需要降级。</summary>
public sealed record LauncherUpdate(Version Version, Uri ReleaseUri)
{
    public string Describe(string currentVersion)
    {
        var local = currentVersion.Split('+')[0];
        if (!System.Version.TryParse(local, out var current))
        {
            return $"当前为开发版 · 最新正式版 v{Version}（仅供查看）";
        }

        return Version > current
            ? $"发现启动器新版本 v{Version}（当前 v{current}）"
            : $"当前启动器 v{current} 已是最新版本。";
    }
}

/// <summary>只读取公开发行信息，不下载或安装程序。</summary>
public interface ILauncherUpdateSource
{
    Task<LauncherUpdate?> GetLatestAsync(CancellationToken cancellationToken);
}
