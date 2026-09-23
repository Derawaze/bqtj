using System.Globalization;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace BqtjLauncher.Infrastructure;

public sealed partial class SqliteGameProfileRepository : IGameProfileRepository, IAccountEditor
{
    private readonly string _connectionString;

    public SqliteGameProfileRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS game_profiles (
                id TEXT PRIMARY KEY NOT NULL,
                display_name TEXT NOT NULL,
                browser_profile_name TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL,
                last_launched_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS account_credentials (
                account_id TEXT PRIMARY KEY NOT NULL REFERENCES game_profiles(id) ON DELETE CASCADE,
                username TEXT NOT NULL,
                password TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<GameProfile>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, browser_profile_name, created_utc, last_launched_utc
            FROM game_profiles
            ORDER BY COALESCE(last_launched_utc, created_utc) DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadProfile(reader));
        }

        return results;
    }

    public async Task<GameProfile?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, browser_profile_name, created_utc, last_launched_utc
            FROM game_profiles
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    public async Task UpsertAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO game_profiles (
                id, display_name, browser_profile_name, created_utc, last_launched_utc)
            VALUES ($id, $displayName, $browserProfileName, $createdUtc, $lastLaunchedUtc)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                browser_profile_name = excluded.browser_profile_name,
                created_utc = excluded.created_utc,
                last_launched_utc = excluded.last_launched_utc;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue("$browserProfileName", profile.BrowserProfileName);
        command.Parameters.AddWithValue(
            "$createdUtc",
            profile.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$lastLaunchedUtc",
            profile.LastLaunchedAtUtc is null
                ? DBNull.Value
                : profile.LastLaunchedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM game_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static GameProfile ReadProfile(SqliteDataReader reader)
    {
        var lastLaunched = reader.IsDBNull(4)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(
                reader.GetString(4),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        return GameProfile.Restore(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(
                reader.GetString(3),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            lastLaunched);
    }
}
