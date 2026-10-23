using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>无材料局须看到可信掉落区，不能把任意空场或补给当成首领掉落。</summary>
public sealed class SkyTopLootSurveyTests
{
    private static readonly ScriptRect Hero = new(200, 290, 12, 20);
    private static SkyTopDropVision.Drop Item(SkyTopDropVision.DropKind kind, int x, int y, bool pickup = false)
        => new(kind, kind.ToString(), new(x, y, 24, 24), 1, false, pickup);
    [Fact]
    public void MaterialAreaRemainsKnownAfterAllMaterialIconsDisappear()
    {
        var survey = new SkyTopLootSurvey();
        Assert.True(survey.Observe([Item(SkyTopDropVision.DropKind.ColdGem, 400, 200, true)], Hero));
        Assert.True(survey.Observe([], Hero));
    }
    [Fact]
    public void VisibleEquipmentClusterSupportsARoundWithNoPriorityMaterials()
    {
        var survey = new SkyTopLootSurvey();
        Assert.True(survey.Observe([Item(SkyTopDropVision.DropKind.Weapon, 400, 200), Item(SkyTopDropVision.DropKind.Equipment, 450, 200)], Hero));
    }
    [Fact]
    public void EmptySceneAndAmmoAreInsufficientAreaEvidence()
    {
        var survey = new SkyTopLootSurvey();
        Assert.False(survey.Observe([], Hero));
        Assert.False(survey.Observe([Item(SkyTopDropVision.DropKind.AmmoBox, 400, 200), Item(SkyTopDropVision.DropKind.HealthPotion, 450, 200)], Hero));
    }
    [Fact]
    public void ClusterBehindPlayerOrFixedRewardBarDoesNotCountAsCovered()
    {
        var survey = new SkyTopLootSurvey();
        Assert.False(survey.Observe([Item(SkyTopDropVision.DropKind.Weapon, 210, 290), Item(SkyTopDropVision.DropKind.Equipment, 230, 290)], Hero));
        Assert.False(survey.Observe([Item(SkyTopDropVision.DropKind.Weapon, 400, 30), Item(SkyTopDropVision.DropKind.Equipment, 440, 30)], Hero));
    }
}
