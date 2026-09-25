using System.Diagnostics.CodeAnalysis;
using System.Net;
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
        "^v[0-9]{3,5}[a-z]?\\.htm$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedFileNamePattern();

    [GeneratedRegex(
        "v(?<version>[0-9]{3,5}[a-z]?)\\.(?:htm|swf)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionLabelPattern();

    /// <summary>
    /// 解析官方页中的游戏入口。先校验各个iframe，页面结构若改版再按 _strGamePath 取，最后退化为
    /// 直接找指向 upload_swf 的地址；均拿不到就返回 false 交给上层回退。
    /// </summary>
    public static bool TryExtractEntryUri(string? html, [NotNullWhen(true)] out Uri? entryUri)
    {
        entryUri = null;
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        // 广告或其它游戏 iframe 可能排在前面，逐个校验而非只检查首个候选。
        foreach (Match match in IframePattern().Matches(html))
        {
            if (TryValidate(match.Groups["url"].Value, out entryUri))
            {
                return true;
            }
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
    /// 严格校验候选地址：必须是 HTTPS 的 4399 绝对地址，路径落在本游戏固定资源目录内，
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
            || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0
            || !AllowedHosts.Contains(parsed.Host, StringComparer.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(parsed.AbsolutePath)?.Replace('\\', '/'),
                "/4399swf/upload_swf/ftp15/linxy/20150324/gun", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>页面里同时存在协议相对地址（//host/path），统一补成 HTTPS 再校验。</summary>
    private static string Normalize(string candidate)
    {
        var value = WebUtility.HtmlDecode(candidate).Trim();
        return value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value;
    }
}
