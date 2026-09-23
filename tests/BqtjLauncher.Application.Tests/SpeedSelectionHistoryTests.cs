using BqtjLauncher.Domain;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class SpeedSelectionHistoryTests
{
    [Fact]
    public void SuccessfulTransitionsKeepThePreviousGearForRepeatedToggle()
    {
        var history = new SpeedSelectionHistory(previous: null);
        var original = SpeedMultiplier.Original;
        var slow = SpeedMultiplier.Create(0.2m);
        var slower = SpeedMultiplier.Create(0.03m);

        history.RecordSuccessfulTransition(original, slow);
        history.RecordSuccessfulTransition(slow, slower);

        Assert.Equal(slow, history.Previous);

        history.RecordSuccessfulTransition(slower, history.Previous!.Value);

        Assert.Equal(slower, history.Previous);
    }

    [Fact]
    public void SelectingTheCurrentGearDoesNotOverwritePreviousGear()
    {
        var previous = SpeedMultiplier.Create(0.2m);
        var current = SpeedMultiplier.Create(0.03m);
        var history = new SpeedSelectionHistory(previous);

        history.RecordSuccessfulTransition(current, current);

        Assert.Equal(previous, history.Previous);
    }
}
