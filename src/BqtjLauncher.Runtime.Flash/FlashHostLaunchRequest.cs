namespace BqtjLauncher.Runtime.Flash;

public sealed record FlashHostLaunchRequest(
    Guid AccountId,
    string AccountName,
    Uri GamePageUri,
    int PanelProcessId,
    bool LayoutProbeEnabled,
    bool IsolationCompatibilityAudioDisabled)
{
    public const string ModeArgument = "--flash-host";

    public static bool TryParse(string[] arguments, out FlashHostLaunchRequest? request)
    {
        request = null;
        if (!arguments.Contains(ModeArgument, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var accountId = ReadValue(arguments, "--account-id");
        var accountName = ReadValue(arguments, "--account-name");
        var gamePage = ReadValue(arguments, "--game-page");
        var panelProcessId = ReadValue(arguments, "--panel-pid");
        if (!Guid.TryParse(accountId, out var parsedId)
            || string.IsNullOrWhiteSpace(accountName)
            || !Uri.TryCreate(gamePage, UriKind.Absolute, out var parsedPage)
            || parsedPage.Scheme is not ("http" or "https")
            || !int.TryParse(panelProcessId, out var parsedPanelProcessId)
            || parsedPanelProcessId <= 0)
        {
            throw new ArgumentException("游戏容器启动参数无效。");
        }

        request = new FlashHostLaunchRequest(
            parsedId,
            accountName.Trim(),
            parsedPage,
            parsedPanelProcessId,
            arguments.Contains("--layout-probe", StringComparer.OrdinalIgnoreCase),
            arguments.Contains(
                "--isolation-compatibility-skip-audio",
                StringComparer.OrdinalIgnoreCase));
        return true;
    }

    private static string? ReadValue(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
