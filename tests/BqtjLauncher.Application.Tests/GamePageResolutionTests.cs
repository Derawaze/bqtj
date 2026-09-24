using System.IO;
using System.Net.Http;
using BqtjLauncher.Application;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>
/// 锁定“平台当前版本”解析契约。游戏入口由平台按版本发布，写死某一版会让启动器
/// 停在旧版本；这里保证官方页解析、镜像映射和回退顺序不被改回常量。
/// </summary>
public sealed class GamePageResolutionTests
{
    private const string OfficialPage = "https://www.4399.com/flash/130396.htm";

    /// <summary>取自 4399 官方游戏页的真实结构（含单引号 iframe 与 _strGamePath）。</summary>
    private const string OfficialHtml = """
        <div id="pusher"></div><iframe id='flash22' align='center' width='950' height='600'
        src='https://sda.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm'
        frameborder='no' scrolling='no'></iframe>
        <script>var _strGamePath="https://sda.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm";</script>
        """;

    [Fact]
    public void ExtractsVersionedEntryFromOfficialGamePage()
    {
        Assert.True(GamePageHtml.TryExtractEntryUri(OfficialHtml, out var entry));

        Assert.Equal(
            "https://sda.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm",
            entry.AbsoluteUri);
        Assert.Equal("3690g", GamePageHtml.ReadVersionLabel(entry));
    }

    [Fact]
    public void FallsBackToGamePathWhenIframeIsMarkupVaried()
    {
        const string html = """
            <script>var _strGamePath = '//sda.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3691a.htm';</script>
            """;

        Assert.True(GamePageHtml.TryExtractEntryUri(html, out var entry));
        Assert.Equal("v3691a.htm", Path.GetFileName(entry.AbsolutePath));
    }

    [Theory]
    // 相似域名、非 HTTPS、站内其它路径和未带版本的文件名都不得通过。
    [InlineData("https://sda.4399.com.evil.test/4399swf/upload_swf/gun/v3690g.htm")]
    [InlineData("http://sda.4399.com/4399swf/upload_swf/gun/v3690g.htm")]
    [InlineData("https://example.com/4399swf/upload_swf/gun/v3690g.htm")]
    [InlineData("https://sda.4399.com/4399swf/upload_swf/gun/game.htm")]
    [InlineData("https://sda.4399.com/flash/130396.htm")]
    public void RejectsEntriesOutsideTheVerifiedShape(string candidate)
    {
        Assert.False(GamePageHtml.TryValidate(candidate, out _));
    }

    [Fact]
    public void PinnedFallbackIsAlwaysUsableOffline()
    {
        Assert.True(GamePageHtml.TryValidate(
            GamePageDefaults.PinnedGamePageUri.AbsoluteUri,
            out var pinned));
        Assert.Equal("3690g", GamePageHtml.ReadVersionLabel(pinned));
    }

    [Fact]
    public async Task UsesOfficialVersionAndMovesItToPlayableHost()
    {
        var resolver = new GamePageResolver((_, _) => Task.FromResult<string?>(OfficialHtml));

        var resolution = await resolver.ResolveAsync(
            new Uri(OfficialPage),
            GamePageDefaults.PinnedGamePageUri);

        Assert.Equal(GamePageResolutionSource.OfficialPage, resolution.Source);
        Assert.Equal("3690g", resolution.VersionLabel);
        // sda 对无 Referer 的请求返回错误页，因此入口改用同路径的 sbai 主机。
        Assert.Equal("sbai.4399.com", resolution.GamePageUri.Host);
        Assert.Equal(
            "/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm",
            resolution.GamePageUri.AbsolutePath);
        Assert.True(resolution.IsCurrent);
    }

    [Fact]
    public async Task UpdatesEntryWhenPlatformPublishesNewerVersion()
    {
        const string newerHtml = """
            <iframe src='https://sda.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3702c.htm'></iframe>
            """;
        var cache = new InMemoryCache();
        var resolver = new GamePageResolver((_, _) => Task.FromResult<string?>(OfficialHtml));

        var first = await resolver.ResolveAsync(
            new Uri(OfficialPage),
            GamePageDefaults.PinnedGamePageUri,
            cache);
        resolver = new GamePageResolver((_, _) => Task.FromResult<string?>(newerHtml));
        var second = await resolver.ResolveAsync(
            new Uri(OfficialPage),
            GamePageDefaults.PinnedGamePageUri,
            cache);

        Assert.Equal("3690g", first.VersionLabel);
        Assert.Equal("3702c", second.VersionLabel);
        Assert.Equal(GamePageResolutionSource.OfficialPage, second.Source);
        Assert.Equal("3702c", GamePageHtml.ReadVersionLabel(cache.Load()!.GamePageUri));
    }

    [Fact]
    public async Task UsesLastResolvedEntryWhenOfficialPageIsUnavailable()
    {
        var cache = new InMemoryCache();
        var resolved = new GamePageResolver((_, _) => Task.FromResult<string?>(OfficialHtml));
        await resolved.ResolveAsync(new Uri(OfficialPage), GamePageDefaults.PinnedGamePageUri, cache);

        var offline = new GamePageResolver((_, _) => Task.FromResult<string?>(null));
        var resolution = await offline.ResolveAsync(
            new Uri(OfficialPage),
            GamePageDefaults.PinnedGamePageUri,
            cache);

        Assert.Equal(GamePageResolutionSource.Cache, resolution.Source);
        Assert.Equal("3690g", resolution.VersionLabel);
        Assert.True(resolution.IsCurrent);
    }

    [Fact]
    public async Task UsesPinnedEntryWhenPageChangesShapeAndCacheIsEmpty()
    {
        var resolver = new GamePageResolver((_, _) => Task.FromResult<string?>("<html>改版后的页面</html>"));

        var resolution = await resolver.ResolveAsync(
            new Uri(OfficialPage),
            GamePageDefaults.PinnedGamePageUri);

        Assert.Equal(GamePageResolutionSource.PinnedFallback, resolution.Source);
        Assert.Equal(GamePageDefaults.PinnedGamePageUri, resolution.GamePageUri);
    }

    [Fact]
    public async Task NetworkFailureDoesNotThrow()
    {
        var resolver = new GamePageResolver((_, _) =>
            Task.FromException<string?>(new HttpRequestException("离线")));

        var resolution = await resolver.ResolveAsync(
            new Uri(OfficialPage),
            GamePageDefaults.PinnedGamePageUri);

        Assert.Equal(GamePageResolutionSource.PinnedFallback, resolution.Source);
    }

    [Fact]
    public void CacheRoundTripsAndIgnoresCorruptContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bqtj-game-page-{Guid.NewGuid():N}.json");
        try
        {
            var cache = new GamePageCache(path);
            Assert.Null(cache.Load());

            var expected = new Uri(
                "https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3690g.htm");
            var resolvedAt = new DateTimeOffset(2026, 9, 23, 6, 0, 0, TimeSpan.Zero);
            cache.Save(new CachedGamePage(expected, resolvedAt));

            var loaded = cache.Load();
            Assert.NotNull(loaded);
            Assert.Equal(expected, loaded.GamePageUri);
            Assert.Equal(resolvedAt, loaded.ResolvedAtUtc);

            File.WriteAllText(path, "{ 不是 JSON");
            Assert.Null(cache.Load());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class InMemoryCache : IGamePageCache
    {
        private CachedGamePage? _value;

        public CachedGamePage? Load() => _value;

        public void Save(CachedGamePage cached) => _value = cached;
    }
}
