using System.Net.Http;
using System.Text;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>
/// 取回 4399 官方游戏页。页面本身是 GB2312，但唯一需要的是一段 ASCII 地址，
/// 因此按单字节文本无损读取，不依赖系统代码页。
/// </summary>
public sealed class HttpGamePageSource : IGamePageSource, IDisposable
{
    /// <summary>解析入口的超时；平台页面不可达时不拖慢面板启动。</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(8);

    private readonly HttpClient _client;
    private readonly GamePageResolver _resolver;
    private readonly bool _ownsClient;

    public HttpGamePageSource()
        : this(new HttpClient { Timeout = RequestTimeout }, new GamePageCache(), ownsClient: true)
    {
    }

    internal HttpGamePageSource(HttpClient client, IGamePageCache cache, bool ownsClient)
    {
        _client = client;
        _ownsClient = ownsClient;
        _resolver = new GamePageResolver(FetchAsync);
        Cache = cache;
    }

    internal IGamePageCache Cache { get; }

    public Task<GamePageResolution> ResolveAsync(
        Uri sourcePageUri,
        Uri pinnedGamePageUri,
        CancellationToken cancellationToken = default) =>
        _resolver.ResolveAsync(sourcePageUri, pinnedGamePageUri, Cache, cancellationToken);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<string?> FetchAsync(Uri sourcePageUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourcePageUri);
        // 平台对缺少常见浏览器标识的请求可能直接拒绝，这里保持普通浏览器形状。
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return DecodeAsText(bytes);
    }

    /// <summary>
    /// 平台页面声明 GB2312，但 .NET 默认不注册该代码页；而这里只需要提取页面中的
    /// ASCII 地址，因此按 Latin-1 无损映射单字节再匹配，既不依赖额外编码包，
    /// 也不会因中文被误解码而破坏 URL。
    /// </summary>
    private static string DecodeAsText(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}

/// <summary>官方游戏页固定入口；版本号由页面内容决定，不写在这里。</summary>
public static class GamePageDefaults
{
    /// <summary>4399 官方游戏页，版本信息的唯一来源。</summary>
    public static Uri SourcePageUri { get; } = new("https://www.4399.com/flash/130396.htm");

    /// <summary>
    /// 解析全部失败时使用的兜底入口（当前为 v3690g）。它只在离线或平台改版时生效，
    /// 正常运行时会由官方页解析结果覆盖。
    /// </summary>
    public static Uri PinnedGamePageUri { get; } =
        new("https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm");
}
