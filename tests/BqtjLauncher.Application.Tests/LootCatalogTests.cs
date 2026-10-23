using System.IO;
using System.Text.Json.Nodes;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>外置图标目录按地图选目标，并在任务开始时冻结规则；全部使用匿名夹具。</summary>
public sealed class LootCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bqtj-loot-" + Guid.NewGuid().ToString("N"));
    public LootCatalogTests()
    {
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(SkyTopDropVision.ResourceDirectory))
            File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
    }

    [Fact]
    public void DifferentMapUsesOnlyItsOwnTargetsPlusTheThreeGlobalChests()
    {
        Edit(root => root["profiles"]!.AsArray().Add(JsonNode.Parse("""
            {"map":"测试关卡","purpose":"daily-materials","pickup":[],"recognize":["blood-stone"]}
            """)));
        var catalog = SkyTopDropVision.Load("测试关卡", directory: _directory);
        var drops = SkyTopDropVision.Find(ReadFixture(), null, null, catalog);
        Assert.Equal(4, drops.Count);
        Assert.Equal(3, drops.Count(item => item.ShouldPickup));
        Assert.DoesNotContain(drops, item => item.Required);
        Assert.Contains(drops, item => item.Name == "血石" && !item.ShouldPickup);
        Assert.DoesNotContain(drops, item => item.Name == "超能石");
    }

    [Fact]
    public void FileUpdateAppliesNextRunAndDoesNotChangeAnExistingSnapshot()
    {
        var before = SkyTopDropVision.Load("天空顶", directory: _directory);
        Edit(root => root["profiles"]![0]!["pickup"]!.AsArray().Add("super-stone"));
        var after = SkyTopDropVision.Load("天空顶", directory: _directory);
        Assert.False(Assert.Single(SkyTopDropVision.Find(ReadFixture(), null, null, before), item => item.Name == "超能石").ShouldPickup);
        Assert.True(Assert.Single(SkyTopDropVision.Find(ReadFixture(), null, null, after), item => item.Name == "超能石").ShouldPickup);
    }

    [Fact]
    public void MissingReferencedImageFailsRatherThanPretendingTheBattlefieldIsEmpty()
    {
        File.Delete(Path.Combine(_directory, "guard-gem.png"));
        Assert.Throws<FileNotFoundException>(() => SkyTopDropVision.Load("天空顶", directory: _directory));
    }

    private void Edit(Action<JsonNode> edit)
    {
        var path = Path.Combine(_directory, "catalog.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        edit(root); File.WriteAllText(path, root.ToJsonString());
    }
    private static BqtjLauncher.Application.ScriptPixels ReadFixture()
    {
        using var bitmap = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sky-top-new-materials.png"));
        var bytes = new byte[950 * 600 * 3];
        for (var y = 0; y < 600; y++) for (var x = 0; x < 950; x++)
        {
            var value = bitmap.GetPixel(x, y); var at = (y * 950 + x) * 3;
            bytes[at] = value.B; bytes[at + 1] = value.G; bytes[at + 2] = value.R;
        }
        return new(950, 600, bytes);
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
