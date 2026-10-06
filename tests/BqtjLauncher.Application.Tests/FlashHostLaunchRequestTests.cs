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
        };

        var isHost = FlashHostLaunchRequest.TryParse(arguments, out var request);

        Assert.True(isHost);
        Assert.NotNull(request);
        Assert.Equal(accountId, request.AccountId);
        Assert.Equal(4321, request.PanelProcessId);
        Assert.True(request.LayoutProbeEnabled);
        Assert.Null(request.AutomationSessionId);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void RejectsInvalidAutomationSession(string sessionId)
        => Assert.Throws<ArgumentException>(() => FlashHostLaunchRequest.TryParse(Arguments(sessionId), out _));

    [Fact]
    public void CarriesAutomationSessionIdentity()
    {
        var id = Guid.NewGuid();
        Assert.True(FlashHostLaunchRequest.TryParse(Arguments(id.ToString("D")), out var request));
        Assert.Equal(id, request!.AutomationSessionId);
    }

    private static string[] Arguments(string sessionId) =>
        [FlashHostLaunchRequest.ModeArgument, "--account-id", Guid.NewGuid().ToString("D"),
            "--account-name", "虚构后台账号", "--game-page", "https://example.com/game.htm", "--panel-pid", "4321",
            "--automation-session", sessionId];
}
