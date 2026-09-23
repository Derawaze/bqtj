using System.Globalization;
using System.IO;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

internal sealed class SpeedPreferenceStore
{
    private readonly string _filePath;

    public SpeedPreferenceStore(Guid accountId)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _filePath = Path.Combine(
            localData,
            "BqtjLauncher",
            "accounts",
            accountId.ToString("N"),
            "recent-speed.txt");
    }

    public SpeedMultiplier? LoadRecent()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var text = File.ReadAllText(_filePath);
            return SpeedMultiplier.TryParse(text, out var multiplier)
                ? multiplier
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SaveRecent(SpeedMultiplier multiplier)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"recent-speed-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                multiplier.Value.ToString(CultureInfo.InvariantCulture));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
