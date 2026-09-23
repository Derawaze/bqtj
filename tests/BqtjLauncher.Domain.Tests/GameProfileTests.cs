namespace BqtjLauncher.Domain.Tests;

public sealed class GameProfileTests
{
    [Fact]
    public void CreateNormalizesNameAndCreatesStableBrowserProfile()
    {
        var now = new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

        var profile = GameProfile.Create("  主账号  ", now);

        Assert.Equal("主账号", profile.DisplayName);
        Assert.Equal($"profile-{profile.Id:N}", profile.BrowserProfileName);
        Assert.Equal(now, profile.CreatedAtUtc);
        Assert.Null(profile.LastLaunchedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankNames(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => GameProfile.Create(name, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RenamePreservesIdentityAndBrowserProfile()
    {
        var original = GameProfile.Create("账号一", DateTimeOffset.UtcNow);

        var renamed = original.Rename("账号二");

        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal(original.BrowserProfileName, renamed.BrowserProfileName);
        Assert.Equal("账号二", renamed.DisplayName);
    }
}
