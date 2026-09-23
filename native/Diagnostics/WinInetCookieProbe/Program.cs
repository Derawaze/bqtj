using System.Runtime.InteropServices;

// 独立回归入口使用每轮唯一的合成域名，避免旧探针状态造成假阳性。
if (args.Length > 0 && args[0] == "matrix")
{
    return CookieIsolationMatrix.Run(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "matrix-child")
{
    return CookieIsolationMatrix.RunChild(args.Skip(1).ToArray());
}

// 只使用保留的 .invalid 合成域名验证 WinINet 隔离；不访问或输出真实登录态。
const string Url = "http://bqtj-account-isolation.invalid/";
const string CookieName = "BqtjIsolationProbe";
const string GamePageUrl = "https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm";
if (args.Length == 3 && args[0].Equals("isolated", StringComparison.OrdinalIgnoreCase))
{
    return AppContainerProbeRunner.Run(args[1], args[2]);
}

if (args.Length != 1)
{
    return 2;
}

if (args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
{
    var succeeded = InternetSetCookie(
        Url,
        null,
        $"{CookieName}=synthetic; expires=Tue,19-Jan-2038 03:14:07 GMT");
    if (!succeeded)
    {
        Console.Error.WriteLine($"win32={Marshal.GetLastWin32Error()}");
    }
    return succeeded ? 0 : 3;
}

if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
{
    return InternetSetCookie(
        Url,
        null,
        $"{CookieName}=; expires=Sat,01-Jan-2000 00:00:00 GMT")
        ? 0
        : 3;
}

if (args[0].Equals("has", StringComparison.OrdinalIgnoreCase))
{
    uint length = 0;
    _ = InternetGetCookie(Url, CookieName, null, ref length);
    return length > 0 ? 0 : 10;
}

if (args[0].Equals("identity", StringComparison.OrdinalIgnoreCase))
{
    return AppContainerProbeRunner.CheckExpectedIdentityAndInternetCapability();
}

if (args[0].Equals("fetch", StringComparison.OrdinalIgnoreCase)
    || args[0].Equals("fetch-server", StringComparison.OrdinalIgnoreCase))
{
    // 只验证该 AppContainer 的 WinINet 出站访问，不读取响应正文或任何 Cookie 内容。
    // 诊断使用直连，排除 AppContainer 无法读取普通用户 WinINet 代理配置的影响。
    var internet = InternetOpen("BqtjIsolationProbe", 1, null, null, 0);
    if (internet == nint.Zero)
    {
        Console.Error.WriteLine($"internet-open-win32={Marshal.GetLastWin32Error()}");
        return 30;
    }

    try
    {
        var timeoutMilliseconds = 10_000;
        _ = InternetSetOption(internet, 2, ref timeoutMilliseconds, sizeof(int));
        _ = InternetSetOption(internet, 5, ref timeoutMilliseconds, sizeof(int));
        _ = InternetSetOption(internet, 6, ref timeoutMilliseconds, sizeof(int));
        var request = InternetOpenUrl(internet, GamePageUrl, null, 0, 0x84000000, nint.Zero);
        if (request == nint.Zero)
        {
            Console.Error.WriteLine($"internet-url-win32={Marshal.GetLastWin32Error()}");
            return 31;
        }

        _ = InternetCloseHandle(request);
        return 0;
    }
    finally
    {
        _ = InternetCloseHandle(internet);
    }
}

return 2;

[DllImport("wininet.dll", EntryPoint = "InternetSetCookieW", CharSet = CharSet.Unicode, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool InternetSetCookie(string url, string? name, string data);

[DllImport("wininet.dll", EntryPoint = "InternetGetCookieW", CharSet = CharSet.Unicode)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool InternetGetCookie(
    string url,
    string? name,
    char[]? data,
    ref uint dataSize);

[DllImport("wininet.dll", EntryPoint = "InternetOpenW", CharSet = CharSet.Unicode, SetLastError = true)]
static extern nint InternetOpen(
    string agent,
    uint accessType,
    string? proxy,
    string? proxyBypass,
    uint flags);

[DllImport("wininet.dll", EntryPoint = "InternetOpenUrlW", CharSet = CharSet.Unicode, SetLastError = true)]
static extern nint InternetOpenUrl(
    nint internet,
    string url,
    string? headers,
    uint headersLength,
    uint flags,
    nint context);

[DllImport("wininet.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool InternetCloseHandle(nint internet);

[DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool InternetSetOption(nint internet, uint option, ref int buffer, uint bufferLength);
