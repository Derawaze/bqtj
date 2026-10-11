// 仅保留 shared/session 合成 Cookie 回归；每轮使用唯一域名，避免旧状态造成假阳性。
if (args.Length > 0 && args[0] == "matrix")
{
    return CookieIsolationMatrix.Run(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "matrix-child")
{
    return CookieIsolationMatrix.RunChild(args.Skip(1).ToArray());
}

Console.Error.WriteLine("用法：WinInetCookieProbe matrix shared|session");
return 2;
