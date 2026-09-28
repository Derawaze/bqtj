using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BqtjLauncher.Application;

namespace BqtjLauncher.Infrastructure;

/// <summary>读取固定 GitHub 仓库的最新正式版，无令牌、无账号信息；网络失败由界面提示。</summary>
public sealed partial class GitHubLauncherUpdateSource : ILauncherUpdateSource
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _client;

    public GitHubLauncherUpdateSource() : this(SharedClient) { }

    public GitHubLauncherUpdateSource(HttpClient client) => _client = client;

    public async Task<LauncherUpdate?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/Derawaze/bqtj/releases/latest");
        request.Headers.UserAgent.ParseAdd("BqtjLauncher-UpdateCheck/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!ReleaseTag().IsMatch(tag) || !Version.TryParse(tag[1..], out var version))
        {
            throw new JsonException("无法识别正式版本号。");
        }

        // 地址由固定仓库与已验证标签生成；不直接执行远程响应提供的任意链接。
        var releaseUri = new Uri($"https://github.com/Derawaze/bqtj/releases/tag/{tag}");
        return new LauncherUpdate(version, releaseUri);
    }

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();
}
