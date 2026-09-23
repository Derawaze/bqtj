namespace BqtjLauncher.Domain.Tests;

public sealed class SpeedMultiplierTests
{
    [Theory]
    [InlineData("0.01", 0.01)]
    [InlineData("0.03倍", 0.03)]
    [InlineData("2x", 2)]
    [InlineData("100", 100)]
    public void TryParseAcceptsSupportedRange(string text, double expected)
    {
        var parsed = SpeedMultiplier.TryParse(text, out var multiplier);

        Assert.True(parsed);
        Assert.Equal((decimal)expected, multiplier.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.009")]
    [InlineData("100.01")]
    [InlineData("abc")]
    public void TryParseRejectsUnsupportedValues(string text)
    {
        Assert.False(SpeedMultiplier.TryParse(text, out _));
    }

    [Fact]
    public void OriginalHasStableDisplayText()
    {
        Assert.True(SpeedMultiplier.Original.IsOriginal);
        Assert.Equal("原速", SpeedMultiplier.Original.DisplayText);
    }
}
