using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class FlashHostLaunchRequestTests
{
    [Fact]
    public void TryParseCarriesOwningPanelProcessIdIntoContainer()
    {
        var accountId = Guid.NewGuid();
        var arguments = new[]
        {
            FlashHostLaunchRequest.ModeArgument,
            "--account-id", accountId.ToString("D"),
            "--account-name", "主账号",
            "--game-page", "https://example.com/game.htm",
            "--panel-pid", "4321",
            "--layout-probe",
            "--control-session", "a3579c4e-21bc-4715-8500-e146cf7542ef",
        };

        var isHost = FlashHostLaunchRequest.TryParse(arguments, out var request);

        Assert.True(isHost);
        Assert.NotNull(request);
        Assert.Equal(accountId, request.AccountId);
        Assert.Equal(4321, request.PanelProcessId);
        Assert.True(request.LayoutProbeEnabled);
        Assert.Equal(Guid.Parse("a3579c4e-21bc-4715-8500-e146cf7542ef"), request.ControlSessionId);
        Assert.Null(request.AutomationSessionId); // 普通窗口有控制通道，但不会被当作后台启动。
    }
}
