using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BqtjLauncher.Application;

/// <summary>上次成功解析的游戏入口及其时间，用于官方页暂时不可用时回退。</summary>
/// <param name="GamePageUri">成功解析并被采纳的包装页地址。</param>
/// <param name="ResolvedAtUtc">解析成功时间。</param>
public sealed record CachedGamePage(Uri GamePageUri, DateTimeOffset ResolvedAtUtc);

/// <summary>缓存读写端口。实现负责持久化和并发安全，解析逻辑不依赖文件系统。</summary>
public interface IGamePageCache
{
    CachedGamePage? Load();

    void Save(CachedGamePage cached);
}

/// <summary>
/// 取回官方游戏页文本。返回 null 表示本次拿不到页面（离线、超时、平台改版），
/// 由调用方决定回退，不抛异常打断启动。
/// </summary>
public delegate Task<string?> GamePageFetcher(Uri sourcePageUri, CancellationToken cancellationToken);

/// <summary>
/// 版本解析主流程：官方页 → 缓存 → 随包兜底。
/// 平台更新游戏时只改官方页里的入口地址，启动器因此必须每次启动重新解析，
/// 而不能把某一版包装页写死在代码里。
/// </summary>
public sealed class GamePageResolver
{
    /// <summary>
    /// 官方页给出的 sda 主机对无 Referer 的请求返回错误页（防盗链），
    /// 同路径的 sbai 主机可直接加载真实包装页，因此默认按主机做映射。
    /// </summary>
    private readonly string _mirrorHost;

    private readonly GamePageFetcher _fetch;

    public GamePageResolver(GamePageFetcher fetch, string mirrorHost = "sbai.4399.com")
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorHost);
        _fetch = fetch;
        _mirrorHost = mirrorHost;
    }

    public async Task<GamePageResolution> ResolveAsync(
        Uri sourcePageUri,
        Uri pinnedGamePageUri,
        IGamePageCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePageUri);
        ArgumentNullException.ThrowIfNull(pinnedGamePageUri);

        string? fetchFailure = null;
        try
        {
            var html = await _fetch(sourcePageUri, cancellationToken);
            if (GamePageHtml.TryExtractEntryUri(html, out var officialUri)
                && TryMapToPlayableHost(officialUri, out var playableUri))
            {
                var resolution = new GamePageResolution(
                    playableUri,
                    GamePageHtml.ReadVersionLabel(officialUri),
                    GamePageResolutionSource.OfficialPage,
                    "已按平台官方页解析当前版本。");
                TrySave(cache, playableUri);
                return resolution;
            }

            fetchFailure = "官方页没有给出可用的游戏入口。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            fetchFailure = $"读取官方页失败：{exception.Message}";
        }

        var cached = TryLoad(cache);
        if (cached is not null)
        {
            return new GamePageResolution(
                cached.GamePageUri,
                GamePageHtml.ReadVersionLabel(cached.GamePageUri),
                GamePageResolutionSource.Cache,
                $"{fetchFailure}已沿用 {cached.ResolvedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} 解析的版本。");
        }

        return new GamePageResolution(
            pinnedGamePageUri,
            GamePageHtml.ReadVersionLabel(pinnedGamePageUri),
            GamePageResolutionSource.PinnedFallback,
            $"{fetchFailure}本次使用随启动器固定的兜底入口。");
    }

    /// <summary>
    /// 把官方地址换到可直接加载的主机，仅替换主机，路径与查询保持平台给出的版本。
    /// 官方主机已经可用时保持原样，避免无谓改动。
    /// </summary>
    public bool TryMapToPlayableHost(Uri officialUri, out Uri playableUri)
    {
        playableUri = officialUri;
        if (officialUri.Host.Equals(_mirrorHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var builder = new UriBuilder(officialUri)
        {
            Host = _mirrorHost,
            Port = -1,
        };
        var mapped = builder.Uri;
        if (!GamePageHtml.TryValidate(mapped.AbsoluteUri, out var validated))
        {
            return false;
        }

        playableUri = validated;
        return true;
    }

    private static CachedGamePage? TryLoad(IGamePageCache? cache)
    {
        try
        {
            var cached = cache?.Load();
            return cached is not null && GamePageHtml.TryValidate(cached.GamePageUri.AbsoluteUri, out _)
                ? cached
                : null;
        }
        catch (Exception)
        {
            // 缓存损坏或不可读时退回兜底入口，不影响启动。
            return null;
        }
    }

    private static void TrySave(IGamePageCache? cache, Uri gamePageUri)
    {
        try
        {
            cache?.Save(new CachedGamePage(gamePageUri, DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            // 缓存写入失败只影响下次回退质量，不失败本次启动。
        }
    }
}

/// <summary>
/// 解析结果的本地缓存，与运行偏好同目录、同风格（临时文件原子替换 + 跨进程命名互斥）。
/// </summary>
public sealed class GamePageCache : IGamePageCache
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public GamePageCache()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BqtjLauncher",
            "game-page.json"))
    {
    }

    public GamePageCache(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public CachedGamePage? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<CacheDocument>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return document is null
                || document.SchemaVersion != CurrentSchemaVersion
                || !Uri.TryCreate(document.GamePageUri, UriKind.Absolute, out var uri)
                ? null
                : new CachedGamePage(uri, document.ResolvedAtUtc);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(CachedGamePage cached)
    {
        ArgumentNullException.ThrowIfNull(cached);
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"game-page-{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new CacheDocument(
                CurrentSchemaVersion,
                cached.GamePageUri.AbsoluteUri,
                cached.ResolvedAtUtc);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record CacheDocument(
        int SchemaVersion,
        string GamePageUri,
        DateTimeOffset ResolvedAtUtc);
}
