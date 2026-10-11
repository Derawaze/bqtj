using System.Diagnostics;

namespace BqtjLauncher.Desktop;

/// <summary>启动独立工具进程，只传固定模式参数，不在参数中传递账号或密码。</summary>
internal static class AccountToolLauncher
{
    public static void Open()
    {
        if (AccountToolInstance.TryActivate()) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--account-tool");
        Process.Start(start);
    }
}
