using BqtjLauncher.Runtime.Flash;

if (args.Length < 2 || !Guid.TryParse(args[0], out var accountId))
{
    Console.Error.WriteLine(
        "用法：AppContainerDesktopSpike <账号Guid> <self-contained发布目录> [Desktop参数...]。");
    return 2;
}

// 该诊断入口只验证整个 WPF 容器能否在账号 AppContainer 内运行，不读取任何 Cookie。
var result = CompatibilityAppContainerGameLauncher.Start(new CompatibilityAppContainerLaunchRequest(
    accountId,
    Path.GetFullPath(args[1]),
    args.Skip(2).ToArray()));
Console.WriteLine($"pid={result.Process.Id}");
Console.WriteLine($"moniker={result.Moniker}");
Console.WriteLine($"packageSid={result.PackageSid}");
Console.WriteLine($"manifestSha256={result.ManifestHash}");
Console.WriteLine($"payload={result.PayloadDirectory}");
return 0;
