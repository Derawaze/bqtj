using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// 用本地 HTTP 夹具驱动正式 x86 原生宿主中的 IE 控件，验证三会话 Cookie、控件重建和宿主重启。
/// 只设置唯一名称/路径的合成 Cookie；不访问游戏站点，也不输出请求 Cookie 内容。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("用法：NativeCookieSessionProbe <BqtjNativeFlashHost.exe>");
            return 2;
        }
        using var server = new CookieFixture();
        using var parent = new Form { Width = 950, Height = 600, ShowInTaskbar = false };
        var parentHandle = parent.Handle; // 隐藏窗口仅提供有效父 HWND，不操作用户桌面窗口。
        var hosts = new List<Process>();
        var passed = false;
        try
        {
            server.SetHostCookie(clear: false);
            foreach (var account in new[] { "A", "B", "C" })
            {
                var start = new ProcessStartInfo(Path.GetFullPath(args[0]))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]))!,
                };
                foreach (var arg in new[] { "--parent", parentHandle.ToInt64().ToString(CultureInfo.InvariantCulture), "--page", server.Url(account) })
                {
                    start.ArgumentList.Add(arg);
                }
                var process = Process.Start(start) ?? throw new InvalidOperationException("启动原生宿主失败。");
                hosts.Add(process);
                RequireResponse(process, "ready");
            }
            WaitFor(() => server.Counts.Values.Sum() >= 12 && server.Counts.Count == 3
                && server.Counts.Values.All(count => count >= 4), "三账号 HTTP Cookie 往返");
            server.AssertHealthy();

            var before = server.Counts.ToDictionary(item => item.Key, item => item.Value);
            foreach (var process in hosts)
            {
                process.StandardInput.WriteLine("reload");
                process.StandardInput.Flush();
                RequireResponse(process, "reload-ok");
            }
            WaitFor(() => before.All(item => server.Counts[item.Key] >= item.Value + 3), "刷新重建浏览器后的 Cookie");
            server.AssertHealthy();
            server.AssertHostCookieUnchanged();

            // 全部旧宿主退出后，再开 A；首个 HTTP 请求必须再次没有本轮 Cookie。
            foreach (var process in hosts) { StopHost(process); }
            hosts.Clear();
            server.RestartAccount("A");
            var restart = new ProcessStartInfo(Path.GetFullPath(args[0]))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            };
            foreach (var arg in new[] { "--parent", parentHandle.ToInt64().ToString(CultureInfo.InvariantCulture), "--page", server.Url("A") })
            {
                restart.ArgumentList.Add(arg);
            }
            var restarted = Process.Start(restart) ?? throw new InvalidOperationException("重启宿主失败。");
            hosts.Add(restarted);
            RequireResponse(restarted, "ready");
            WaitFor(() => server.Counts["A"] >= 3, "重启后的空会话");
            server.AssertHealthy();
            server.AssertHostCookieUnchanged();
            passed = true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
        }
        finally
        {
            foreach (var process in hosts) { StopHost(process); }
            try { server.SetHostCookie(clear: true); }
            catch (Exception exception) { passed = false; Console.Error.WriteLine(exception.Message); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed, scope = "native-ie-http-cookie-only", concurrentAccounts = 3,
            requests = server.Counts, disconnectedRequests = server.DisconnectedRequests,
            errors = server.Errors.ToArray(),
        }));
        return passed ? 0 : 1;
    }

    private static void RequireResponse(Process process, string expected)
    {
        var actual = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (actual != expected) { throw new InvalidOperationException($"原生回执错误：预期 {expected}，实际 {actual}。"); }
    }

    private static void WaitFor(Func<bool> predicate, string operation)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(20)) { throw new TimeoutException(operation); }
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(50);
        }
    }

    private static void StopHost(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(3000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            }
        }
        finally { process.Dispose(); }
    }
}

/// <summary>只监听回环随机端口；随机 URL 前缀避免读取或修改既有本地站点的 Cookie。</summary>
internal sealed class CookieFixture : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly string _prefix = $"/{Guid.NewGuid():N}/";
    private readonly string _name = $"BqtjNative{Guid.NewGuid():N}";
    private readonly string _root;
    private int _disconnectedRequests;
    public int DisconnectedRequests => Volatile.Read(ref _disconnectedRequests);
    public ConcurrentDictionary<string, int> Counts { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();

    public CookieFixture()
    {
        _listener.Start();
        _root = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_prefix}";
        _server = ServeAsync();
    }

    public string Url(string account) => _root + account;
    public void RestartAccount(string account) => Counts[account] = 0;
    public void AssertHealthy()
    {
        if (!Errors.IsEmpty) { throw new InvalidOperationException("原生 IE 会话 Cookie 检查失败。"); }
    }

    public void SetHostCookie(bool clear)
    {
        var expiry = clear ? "Sat, 01-Jan-2000 00:00:00 GMT" : "Tue, 19-Jan-2038 03:14:07 GMT";
        if (!InternetSetCookie(_root, null, $"{_name}=host; expires={expiry}; path={_prefix}"))
        {
            throw new InvalidOperationException($"合成宿主 Cookie 写入失败：{Marshal.GetLastWin32Error()}。");
        }
        if (!clear) { AssertHostCookieUnchanged(); }
        else
        {
            uint length = 256;
            var buffer = new char[length];
            if (InternetGetCookie(_root, _name, buffer, ref length) || Marshal.GetLastWin32Error() != 259)
            {
                throw new InvalidOperationException("合成宿主 Cookie 清理未确认。");
            }
        }
    }

    public void AssertHostCookieUnchanged()
    {
        uint length = 256;
        var buffer = new char[length];
        if (!InternetGetCookie(_root, _name, buffer, ref length)
            || new string(buffer).TrimEnd('\0') != $"{_name}=host")
        {
            throw new InvalidOperationException("宿主已保存合成 Cookie 被改变。");
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                // IE 会预建空闲连接，必须并发接收，否则空闲连接会阻塞其他账号的请求。
                _connections.Add(HandleConnectionAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { Errors.Enqueue(exception.GetType().Name); }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await reader.ReadLineAsync(timeout.Token);
                if (request is null) { return; }
                string? cookie = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token)))
                {
                    if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                    {
                        cookie = line[7..].Split(';').Select(value => value.Trim())
                            .FirstOrDefault(value => value.StartsWith(_name + "=", StringComparison.Ordinal));
                    }
                }
                var path = request?.Split(' ').ElementAtOrDefault(1);
                var account = path?.StartsWith(_prefix, StringComparison.Ordinal) == true ? path[_prefix.Length..] : "";
                if (account is not ("A" or "B" or "C"))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
                    return;
                }
                var count = Counts.AddOrUpdate(account, 1, (_, previous) => previous + 1);
                if (count == 1 ? cookie is not null : cookie != $"{_name}={account}")
                {
                    Errors.Enqueue($"{account}: request {count} cookie mismatch");
                }
                const string body = "<html><head><meta http-equiv='refresh' content='1'></head><body>Local synthetic cookie fixture</body></html>";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nCache-Control: no-store\r\nConnection: close\r\nSet-Cookie: {_name}={account}; expires=Tue, 19-Jan-2038 03:14:07 GMT; path={_prefix}\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            }
            // 空闲预连接或测试结束时取消，不构成 Cookie 失败；实际请求仍由计数门禁检查。
            catch (OperationCanceledException) { }
            // 控件销毁/刷新可能取消正在返回的 HTTP 响应；单独计数，不冒充 Cookie 断言失败。
            catch (IOException) { Interlocked.Increment(ref _disconnectedRequests); }
            catch (Exception exception) { Errors.Enqueue(exception.GetType().Name); }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _server.GetAwaiter().GetResult();
        Task.WhenAll(_connections).GetAwaiter().GetResult();
        _stop.Dispose();
    }

    [DllImport("wininet.dll", EntryPoint = "InternetSetCookieW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetCookie(string url, string? name, string value);

    [DllImport("wininet.dll", EntryPoint = "InternetGetCookieW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetGetCookie(string url, string name, [Out] char[] data, ref uint length);
}
