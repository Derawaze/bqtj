using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// 通过新进程 A/B/A 验证 WinINet 合成 Cookie 边界；只允许保留域名，不接触平台登录态。
/// 返回 1 表示隔离门禁未通过，2 表示探针本身出错，不能把 API 失败当作隔离成功。
/// </summary>
internal static class CookieIsolationMatrix
{
    private const string CookieName = "BqtjMatrix";
    private const int NotFound = 10;
    private static bool _sessionInitialized;

    public static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "session")
        {
            return RunSessionMatrix();
        }

        if (args.Length != 1 || args[0] != "shared")
        {
            Console.Error.WriteLine("用法：WinInetCookieProbe matrix shared|session");
            return 2;
        }

        var mode = args[0];
        var url = $"http://{Guid.NewGuid():N}.bqtj-isolation.invalid/";
        var result = 2;
        bool? isolated = null;
        bool? persistent = null;
        try
        {
            // 同一 URL、同一 Cookie 名只改变账号标签；标签不能被误当成真实隔离边界。
            Expect(mode, url, "A", "has", NotFound);
            Expect(mode, url, "A", "set", 0);
            var b = Invoke(mode, url, "B", "has");
            var a = Invoke(mode, url, "A", "has");
            if ((b != 0 && b != NotFound) || (a != 0 && a != NotFound))
            {
                throw new InvalidOperationException($"读取探针异常：B={b}，A={a}。");
            }

            isolated = b == NotFound;
            persistent = a == 0;
            result = isolated.Value && persistent.Value ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
        }
        finally
        {
            // 仅过期本轮唯一合成 Cookie，不清理用户 Cookie/缓存，也不枚举其他域名。
            try
            {
                Expect(mode, url, "A", "clear", 0);
                Expect(mode, url, "A", "has", NotFound);
            }
            catch (Exception exception)
            {
                result = 2;
                Console.Error.WriteLine($"合成 Cookie 清理失败：{exception.Message}");
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode,
            isolated,
            persistent,
            passed = result == 0,
            probeError = result == 2,
            scope = "synthetic-cookie-only",
        }));
        return result;
    }

    /// <summary>每次操作使用全新进程，避免进程内缓存冒充重启持久化。</summary>
    private static int Invoke(string mode, string url, string account, string operation)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定探针路径。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in new[] { "matrix-child", mode, url, account, operation })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动探针子进程。");
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Cookie 探针超时。");
        }

        return process.ExitCode;
    }

    private static void Expect(string mode, string url, string account, string operation, int expected)
    {
        var actual = Invoke(mode, url, account, operation);
        if (actual != expected)
        {
            throw new InvalidOperationException($"探针前置条件失败：{account}/{operation}={actual}，预期 {expected}。");
        }
    }

    /// <summary>在校验过的合成域名上执行操作；真实域名在调用 WinINet 前被拒绝。</summary>
    public static int RunChild(string[] args)
    {
        if ((args.Length != 4 && args.Length != 5)
            || (args[0] != "shared" && args[0] != "sandboxie" && args[0] != "session")
            || !Uri.TryCreate(args[1], UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || !uri.Host.EndsWith(".bqtj-isolation.invalid", StringComparison.Ordinal)
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || !uri.IsDefaultPort
            || (args[2] != "A" && args[2] != "B"))
        {
            return 2;
        }

        // 必须查询当前进程的实际沙箱，不能仅凭命令行标签或启动器退出成功认定隔离。
        if (args[0] == "sandboxie")
        {
            if (args.Length != 5 || !IsInExpectedSandbox(args[4]))
            {
                Console.Error.WriteLine("sandbox-identity-mismatch");
                return 2;
            }
        }

        if (args[0] == "session" && !_sessionInitialized)
        {
            // 仅探针：在第一次 Cookie 访问前抑制持久化，不更改系统 Internet 选项。
            uint behavior = 3; // INTERNET_SUPPRESS_COOKIE_PERSIST
            if (!InternetSetOption(nint.Zero, 81, ref behavior, sizeof(uint)))
            {
                Console.Error.WriteLine($"suppress-win32={Marshal.GetLastWin32Error()}");
                return 2;
            }
            // WinINet 重复设置该选项可能重置进程 Cookie；只在会话启动时设置一次。
            _sessionInitialized = true;
        }

        var operation = args[3];
        if (operation == "serve" && args[0] == "session")
        {
            // 两个长驻进程交替读写同名 Cookie，检验运行时串号，而不只测先后启动。
            Console.WriteLine("ready");
            string? command;
            while ((command = Console.ReadLine()) is not null && command != "exit")
            {
                if (command != "has" && command != "set" && command != "matches")
                {
                    return 2;
                }
                Console.WriteLine(RunChild([args[0], args[1], args[2], command]));
            }
            return 0;
        }
        if (operation == "has")
        {
            return HasCookie(uri.AbsoluteUri);
        }

        if (operation == "matches")
        {
            return MatchesCookie(uri.AbsoluteUri, args[2]);
        }

        if (operation != "set" && operation != "clear")
        {
            return 2;
        }

        var data = operation == "set"
            ? $"{CookieName}=synthetic-{args[2]}; expires=Tue,19-Jan-2038 03:14:07 GMT; path=/"
            : $"{CookieName}=; expires=Sat,01-Jan-2000 00:00:00 GMT; path=/";
        if (!InternetSetCookie(uri.AbsoluteUri, null, data))
        {
            Console.Error.WriteLine($"set-win32={Marshal.GetLastWin32Error()}");
            return 2;
        }

        // 写入后立即检查可见性，防止“全部 Cookie 都不可用”被误判为隔离。
        return operation == "set" ? MatchesCookie(uri.AbsoluteUri, args[2]) : 0;
    }

    /// <summary>验证临时会话独立、宿主旧 Cookie 不受影响，以及退出后新会话从空状态开始。</summary>
    private static int RunSessionMatrix()
    {
        var url = $"http://{Guid.NewGuid():N}.bqtj-isolation.invalid/";
        var passed = false;
        var error = false;
        var observations = new List<object>();
        void Check(string step, int actual, int expected)
        {
            observations.Add(new { step, actual, expected });
            if (actual != expected)
            {
                error = actual != 0 && actual != 10 && actual != 11;
                throw new InvalidOperationException($"{step}: expected={expected}, actual={actual}");
            }
        }
        try
        {
            Expect("shared", url, "A", "set", 0);
            using (var a = new SessionWorker(url, "A"))
            using (var b = new SessionWorker(url, "B"))
            {
                Check("A ignores saved host cookie", a.Command("has"), 10);
                Check("B ignores saved host cookie", b.Command("has"), 10);
                Check("A writes own cookie", a.Command("set"), 0);
                Check("B cannot see A", b.Command("has"), 10);
                Check("B writes own cookie", b.Command("set"), 0);
                Check("A retains own cookie", a.Command("matches"), 0);
                Check("B retains own cookie", b.Command("matches"), 0);
                Check("host saved cookie unchanged", Invoke("shared", url, "A", "matches"), 0);
            }
            using var restarted = new SessionWorker(url, "A");
            Check("restart starts without cookie", restarted.Command("has"), 10);
            passed = true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            if (observations.Count == 0) { error = true; }
        }
        finally
        {
            try
            {
                Expect("shared", url, "A", "clear", 0);
                Expect("shared", url, "A", "has", NotFound);
            }
            catch (Exception exception)
            {
                passed = false;
                error = true;
                Console.Error.WriteLine($"synthetic cleanup: {exception.Message}");
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "session", passed, probeError = error, observations }));
        return passed ? 0 : error ? 2 : 1;
    }

    /// <summary>以匿名管道控制本轮专用子进程，结束后释放；不操作已有游戏进程。</summary>
    private sealed class SessionWorker : IDisposable
    {
        private readonly Process _process;

        public SessionWorker(string url, string account)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到探针路径。");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }
            foreach (var argument in new[] { "matrix-child", "session", url, account, "serve" })
            {
                start.ArgumentList.Add(argument);
            }
            _process = Process.Start(start) ?? throw new InvalidOperationException("探针子进程启动失败。");
            try
            {
                if (ReadResponse() != "ready") { throw new InvalidOperationException("会话探针未就绪。"); }
            }
            catch { Dispose(); throw; }
        }

        public int Command(string command)
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
            return int.TryParse(ReadResponse(), out var result) ? result : 2;
        }

        private string? ReadResponse() => _process.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000))
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit();
                    }
                }
            }
            finally { _process.Dispose(); }
        }
    }

    /// <summary>仅比较本轮合成值而不输出内容，检出 B 写入后覆盖 A 的反向串号。</summary>
    private static int MatchesCookie(string url, string account)
    {
        uint length = 256;
        var data = new char[length];
        if (!InternetGetCookieValue(url, CookieName, data, ref length))
        {
            return Marshal.GetLastWin32Error() == 259 ? NotFound : 2;
        }

        return new string(data).TrimEnd('\0') == $"{CookieName}=synthetic-{account}" ? 0 : 11;
    }

    /// <summary>只查询已经由沙箱加载的模块，不主动加载第三方 DLL，不尝试普通进程回退。</summary>
    private static bool IsInExpectedSandbox(string expected)
    {
        if (expected != "BqtjCookieProbeA" && expected != "BqtjCookieProbeB")
        {
            return false;
        }

        var module = GetModuleHandle("SbieDll.dll");
        if (module == nint.Zero)
        {
            return false;
        }

        if (!NativeLibrary.TryGetExport(module, "SbieApi_QueryProcess", out var address))
        {
            return false;
        }

        var query = Marshal.GetDelegateForFunctionPointer<QuerySandboxProcess>(address);
        var box = new StringBuilder(256);
        var result = query((nint)Environment.ProcessId, box, nint.Zero, nint.Zero, nint.Zero);
        return result == 0 && string.Equals(box.ToString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    // SbieApi 公共头文件声明为 C 调用约定；x86 下不能沿用默认 Winapi/StdCall。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int QuerySandboxProcess(nint processId, StringBuilder box, nint image, nint sid, nint session);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string name);

    private static int HasCookie(string url)
    {
        uint length = 0;
        var succeeded = InternetGetCookie(url, CookieName, nint.Zero, ref length);
        var error = Marshal.GetLastWin32Error();
        if (length > 0 && (succeeded || error == 122))
        {
            return 0;
        }

        if (!succeeded && error == 259)
        {
            return NotFound;
        }

        Console.Error.WriteLine($"get-win32={error}; length={length}");
        return 2;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetSetCookieW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetCookie(string url, string? name, string data);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(nint internet, uint option, ref uint buffer, int length);

    [DllImport("wininet.dll", EntryPoint = "InternetGetCookieW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetGetCookie(string url, string name, nint data, ref uint length);

    [DllImport("wininet.dll", EntryPoint = "InternetGetCookieW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetGetCookieValue(string url, string name, [Out] char[] data, ref uint length);
}
