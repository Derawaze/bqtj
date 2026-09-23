using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>游戏容器支持的窗口档位；该值会作为所有账号共享的本地偏好持久化。</summary>
internal enum GameWindowMode
{
    Original,
    Scale150,
    Scale200,
    FullScreen,
}

/// <summary>跨账号共享的游戏运行偏好。</summary>
internal sealed record GameRuntimePreferences(bool IsMuted, GameWindowMode WindowMode)
{
    public static GameRuntimePreferences Default { get; } = new(false, GameWindowMode.Original);
}

/// <summary>
/// 统一读写全局游戏运行偏好，并通过跨进程互斥和原子替换避免多开时相互覆盖。
/// </summary>
internal sealed class GameRuntimePreferenceStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly string _mutexName;

    public GameRuntimePreferenceStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BqtjLauncher",
            "runtime-preferences.json"))
    {
    }

    internal GameRuntimePreferenceStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_filePath.ToUpperInvariant())));
        _mutexName = $"Local\\BqtjLauncher.RuntimePreferences.{pathHash}";
    }

    public GameRuntimePreferences Load() => WithLock(LoadCore);

    public void SetMuted(bool isMuted) => Update(current => current with { IsMuted = isMuted });

    public void SetWindowMode(GameWindowMode windowMode)
    {
        if (!Enum.IsDefined(windowMode))
        {
            throw new ArgumentOutOfRangeException(nameof(windowMode));
        }

        Update(current => current with { WindowMode = windowMode });
    }

    private void Update(Func<GameRuntimePreferences, GameRuntimePreferences> update) =>
        WithLock(() =>
        {
            WriteCore(update(LoadCore()));
            return true;
        });

    private GameRuntimePreferences LoadCore()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return GameRuntimePreferences.Default;
            }

            var document = JsonSerializer.Deserialize<PreferenceDocument>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            if (document is null
                || document.SchemaVersion != CurrentSchemaVersion
                || !Enum.IsDefined(document.WindowMode))
            {
                return GameRuntimePreferences.Default;
            }

            return new GameRuntimePreferences(document.IsMuted, document.WindowMode);
        }
        catch (JsonException)
        {
            return GameRuntimePreferences.Default;
        }
        catch (IOException)
        {
            return GameRuntimePreferences.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return GameRuntimePreferences.Default;
        }
    }

    private void WriteCore(GameRuntimePreferences preferences)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"runtime-preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new PreferenceDocument(
                CurrentSchemaVersion,
                preferences.IsMuted,
                preferences.WindowMode);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, SerializerOptions));
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

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                /* 前一容器异常退出后当前进程已经取得互斥体，可继续恢复偏好文件。 */
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new IOException("等待游戏运行偏好文件超时。");
            }

            return action();
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private sealed record PreferenceDocument(
        int SchemaVersion,
        bool IsMuted,
        GameWindowMode WindowMode);
}
