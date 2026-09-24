using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace BqtjLauncher.Application;

/// <summary>
/// 从 4399 官方游戏页 HTML 中取出游戏入口地址。页面只提供“当前发布版本”的地址，
/// 版本号本身写在包装页文件名里（例如 v3690g.htm），所以这里是版本检测的唯一解析点。
/// 只做取地址与白名单校验，不发请求、不写状态，便于离线测试。
/// </summary>
public static partial class GamePageHtml
{
    /// <summary>可承载游戏包装页的 4399 主机。非 4399 域名一律拒绝。</summary>
    private static readonly string[] AllowedHosts =
    [
        "sda.4399.com",
        "sbai.4399.com",
        "www.4399.com",
    ];

    [GeneratedRegex(
        "<iframe[^>]*src\\s*=\\s*(['\"])(?<url>[^'\"]+)\\1[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IframePattern();

    [GeneratedRegex(
        "_strGamePath\\s*=\\s*(['\"])(?<url>[^'\"]+)\\1",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GamePathPattern();

    [GeneratedRegex(
        "(?<url>https?://[^\"'\\s<>]*(?:sda|sbai|www)\\.4399\\.com/[^\"'\\s<>]*upload_swf[^\"'\\s<>]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerUrlPattern();

    [GeneratedRegex(
        "v[0-9]{3,5}[a-z]?\\.(?:htm|swf)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedFileNamePattern();

    [GeneratedRegex(
        "v(?<version>[0-9]{3,5}[a-z]?)\\.(?:htm|swf)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionLabelPattern();

    /// <summary>
    /// 解析官方页中的游戏入口。页面结构若改版，先按 _strGamePath 取，再退化为
    /// 直接找指向 upload_swf 的地址；两者都拿不到就返回 false 交给上层回退。
    /// </summary>
    public static bool TryExtractEntryUri(string? html, [NotNullWhen(true)] out Uri? entryUri)
    {
        entryUri = null;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (TryValidate(FindIframeSource(html), out entryUri))
        {
            return true;
        }

        foreach (Match match in GamePathPattern().Matches(html))
        {
            if (TryValidate(match.Groups["url"].Value, out entryUri))
            {
                return true;
            }
        }

        foreach (Match match in PlayerUrlPattern().Matches(html))
        {
            if (TryValidate(Normalize(match.Groups["url"].Value), out entryUri))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 严格校验候选地址：必须是 HTTPS 的 4399 绝对地址，路径落在上传目录内，
    /// 且文件名是版本化包装页。宁可放弃本次自动更新，也不把任意地址交给原生宿主。
    /// </summary>
    public static bool TryValidate(string? candidate, [NotNullWhen(true)] out Uri? entryUri)
    {
        entryUri = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!Uri.TryCreate(Normalize(candidate), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !AllowedHosts.Contains(parsed.Host, StringComparer.OrdinalIgnoreCase)
            || !parsed.AbsolutePath.Contains("/upload_swf/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(parsed.AbsolutePath);
        if (!VersionedFileNamePattern().IsMatch(fileName))
        {
            return false;
        }

        entryUri = parsed;
        return true;
    }

    /// <summary>取出版本标记（如 v3690g.htm → 3690g），取不到返回 null。</summary>
    public static string? ReadVersionLabel(Uri? gamePageUri)
    {
        if (gamePageUri is null)
        {
            return null;
        }

        var match = VersionLabelPattern().Match(gamePageUri.AbsolutePath);
        return match.Success ? match.Groups["version"].Value.ToLowerInvariant() : null;
    }

    private static string? FindIframeSource(string html)
    {
        foreach (Match match in IframePattern().Matches(html))
        {
            var url = match.Groups["url"].Value;
            // 官方页会同时挂统计/广告 iframe，只认装载 Flash 播放器的那个。
            if (url.Contains("upload_swf", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>页面里同时存在协议相对地址（//host/path），统一补成 HTTPS 再校验。</summary>
    private static string Normalize(string candidate)
    {
        var value = candidate.Trim();
        return value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value;
    }
}
