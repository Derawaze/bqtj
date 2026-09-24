namespace BqtjLauncher.Application;

/// <summary>本次解析出游戏入口地址的来源，用于面板文案和排障，不参与会话逻辑。</summary>
public enum GamePageResolutionSource
{
    /// <summary>成功读取平台官方游戏页，拿到当前发布的入口。</summary>
    OfficialPage,

    /// <summary>官方页不可用时沿用上次成功解析并缓存的入口。</summary>
    Cache,

    /// <summary>官方页和缓存都不可用，使用随启动器固定的兜底入口。</summary>
    PinnedFallback,
}

/// <summary>
/// 一次游戏入口解析的结果。游戏由平台按版本发布包装页，版本号写在文件名里，
/// 因此必须把“当前版本”当成运行时数据而不是编译期常量。
/// </summary>
/// <param name="GamePageUri">最终交给原生宿主导航的包装页地址。</param>
/// <param name="VersionLabel">解析出的版本标记（如 3690g）；无法识别时为 null。</param>
/// <param name="Source">该地址的来源。</param>
/// <param name="Detail">面向用户的说明，可为空。</param>
public sealed record GamePageResolution(
    Uri GamePageUri,
    string? VersionLabel,
    GamePageResolutionSource Source,
    string? Detail = null)
{
    /// <summary>是否取到了平台当前发布的版本，而不是兜底值。</summary>
    public bool IsCurrent => Source != GamePageResolutionSource.PinnedFallback;
}

/// <summary>
/// 解析平台当前发布的游戏入口。实现负责取回官方游戏页并把版本化地址翻译成
/// 本机可用的地址；调用方只依赖这个结果，不感知平台页面结构。
/// </summary>
public interface IGamePageSource
{
    /// <param name="sourcePageUri">平台官方游戏页，版本信息的唯一来源。</param>
    /// <param name="pinnedGamePageUri">官方页不可用时使用的兜底入口。</param>
    Task<GamePageResolution> ResolveAsync(
        Uri sourcePageUri,
        Uri pinnedGamePageUri,
        CancellationToken cancellationToken = default);
}
