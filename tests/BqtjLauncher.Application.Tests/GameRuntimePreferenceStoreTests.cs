using System.IO;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class GameRuntimePreferenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"bqtj-runtime-preferences-{Guid.NewGuid():N}");

    [Fact]
    public void MissingFileUsesSafeDefaults()
    {
        var store = CreateStore();

        var preferences = store.Load();

        Assert.False(preferences.IsMuted);
        Assert.Equal(GameWindowMode.Original, preferences.WindowMode);
    }

    [Fact]
    public void SeparateAccountsShareSettingsWithoutOverwritingOtherField()
    {
        var firstAccount = CreateStore();
        var secondAccount = CreateStore();

        firstAccount.SetMuted(true);
        secondAccount.SetWindowMode(GameWindowMode.Scale150);

        var preferences = firstAccount.Load();
        Assert.True(preferences.IsMuted);
        Assert.Equal(GameWindowMode.Scale150, preferences.WindowMode);
    }

    [Fact]
    public void InvalidFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "preferences.json"), "not-json");

        var preferences = CreateStore().Load();

        Assert.Equal(GameRuntimePreferences.Default, preferences);
    }

    private GameRuntimePreferenceStore CreateStore() =>
        new(Path.Combine(_directory, "preferences.json"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
