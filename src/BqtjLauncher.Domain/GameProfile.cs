namespace BqtjLauncher.Domain;

public sealed record GameProfile
{
    private const int MaxDisplayNameLength = 40;

    private GameProfile(
        Guid id,
        string displayName,
        string browserProfileName,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? lastLaunchedAtUtc)
    {
        Id = id;
        DisplayName = displayName;
        BrowserProfileName = browserProfileName;
        CreatedAtUtc = createdAtUtc;
        LastLaunchedAtUtc = lastLaunchedAtUtc;
    }

    public Guid Id { get; }

    public string DisplayName { get; private init; }

    public string BrowserProfileName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? LastLaunchedAtUtc { get; private init; }

    public static GameProfile Create(string displayName, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        return new GameProfile(
            id,
            NormalizeDisplayName(displayName),
            $"profile-{id:N}",
            now.ToUniversalTime(),
            null);
    }

    public static GameProfile Restore(
        Guid id,
        string displayName,
        string browserProfileName,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? lastLaunchedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserProfileName);

        return new GameProfile(
            id,
            NormalizeDisplayName(displayName),
            browserProfileName,
            createdAtUtc.ToUniversalTime(),
            lastLaunchedAtUtc?.ToUniversalTime());
    }

    public GameProfile Rename(string displayName) =>
        this with { DisplayName = NormalizeDisplayName(displayName) };

    public GameProfile MarkLaunched(DateTimeOffset now) =>
        this with { LastLaunchedAtUtc = now.ToUniversalTime() };

    private static string NormalizeDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalized = displayName.Trim();
        if (normalized.Length > MaxDisplayNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayName),
                $"档案名称不能超过 {MaxDisplayNameLength} 个字符。");
        }

        return normalized;
    }
}
