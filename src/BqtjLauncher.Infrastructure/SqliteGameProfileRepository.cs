using System.Globalization;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace BqtjLauncher.Infrastructure;

/// <summary>本地账号、权益及凭据的持久化入口；初始化事务兼容旧库结构。</summary>
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
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS game_profiles (
                id TEXT PRIMARY KEY NOT NULL,
                display_name TEXT NOT NULL,
                browser_profile_name TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL,
                last_launched_utc TEXT NULL,
                has_president INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS account_credentials (
                account_id TEXT PRIMARY KEY NOT NULL REFERENCES game_profiles(id) ON DELETE CASCADE,
                username TEXT NOT NULL,
                password TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        // 检查与补列处于同一写事务，主面板和独立账号工具同时启动时只升级一次。
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('game_profiles') WHERE name = 'has_president'";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            command.CommandText = "ALTER TABLE game_profiles ADD COLUMN has_president INTEGER NOT NULL DEFAULT 0";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<GameProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<GameProfile>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, browser_profile_name, created_utc, last_launched_utc, has_president
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
            SELECT id, display_name, browser_profile_name, created_utc, last_launched_utc, has_president
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
                id, display_name, browser_profile_name, created_utc, last_launched_utc, has_president)
            VALUES ($id, $displayName, $browserProfileName, $createdUtc, $lastLaunchedUtc, $hasPresident)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                browser_profile_name = excluded.browser_profile_name,
                created_utc = excluded.created_utc,
                last_launched_utc = excluded.last_launched_utc;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        // 已有记录的权益只由账号编辑入口修改；启动时间回写不能覆盖独立工具刚保存的设置。
        command.Parameters.AddWithValue("$hasPresident", profile.HasPresident ? 1 : 0);
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
            lastLaunched, reader.GetInt32(5) != 0);
    }
}
