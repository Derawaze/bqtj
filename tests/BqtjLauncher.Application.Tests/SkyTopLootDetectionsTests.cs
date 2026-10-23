using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>首次定位用同名材料的场景位置复查，动画误报不能成为持久掉落带。</summary>
public sealed class SkyTopLootDetectionsTests
{
    private static SkyTopDropVision.Drop Gem(int x, int width = 20)
        => new(SkyTopDropVision.DropKind.ColdGem, "冷冻宝石", new(x, 200, width, 20), .9, width > 45, true);

    [Fact]
    public void OneFrameFlashNeverEntersThePickupInventory()
    {
        var detector = new SkyTopLootDetections();
        Assert.Empty(detector.Observe([Gem(300)], new(0, 0, 1, 1)));
        Assert.Empty(detector.Observe([], new(0, 0, 1, 1)));
        Assert.False(detector.HasCandidates);
    }

    [Fact]
    public void ConfirmedPileKeepsItsIdentityWhileCameraMovesAndBoundsShrink()
    {
        var detector = new SkyTopLootDetections();
        Assert.Empty(detector.Observe([Gem(300, 140)], new(0, 0, 1, 1)));
        var confirmed = Assert.Single(detector.Observe([Gem(440)], new(20, 0, 1, 1)));
        Assert.Equal(440, confirmed.Bounds.X);
    }

    [Fact]
    public void DistantSameNameFlashDoesNotConfirmAnotherPosition()
    {
        var detector = new SkyTopLootDetections();
        detector.Observe([Gem(300)], new(0, 0, 1, 1));
        Assert.Empty(detector.Observe([Gem(600)], new(0, 0, 1, 1)));
    }

    [Fact]
    public void NewlyRevealedMaterialRequiresConfirmationEvenAfterPreviousTargetsDisappeared()
    {
        var detector = new SkyTopLootDetections();
        detector.Observe([Gem(300)], new(0, 0, 1, 1));
        Assert.Single(detector.Observe([Gem(300)], new(0, 0, 1, 1)));
        Assert.False(detector.NeedsConfirmation);
        Assert.Empty(detector.Observe([Gem(600)], new(0, 0, 1, 1)));
        Assert.True(detector.NeedsConfirmation);
        Assert.Single(detector.Observe([Gem(600)], new(0, 0, 1, 1)));
        Assert.False(detector.NeedsConfirmation);
    }
}
