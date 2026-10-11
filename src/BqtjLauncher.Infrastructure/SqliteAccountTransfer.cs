using BqtjLauncher.Application;
using Microsoft.Data.Sqlite;

namespace BqtjLauncher.Infrastructure;

/// <summary>为批量导入导出读取已保存账号；用户名唯一性与编辑共用同一写事务。</summary>
public sealed partial class SqliteGameProfileRepository : IAccountTransferStore
{
    /// <summary>读取已保存凭据的完整记录；无凭据的手动登录记录不参与三字段导出。</summary>
    public async Task<IReadOnlyList<SavedAccount>> ListSavedAccountsAsync()
    {
        var accounts = new List<SavedAccount>();
        await using var connection = await OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.display_name, c.username, c.password
            FROM game_profiles p JOIN account_credentials c ON c.account_id = p.id
            ORDER BY p.created_utc, p.id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            accounts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return accounts;
    }

    /// <summary>左连接保留无凭据账号，空账号与空密码在管理表格中直接显示为空。</summary>
    public async Task<IReadOnlyList<SavedAccount>> ListAccountsAsync()
    {
        var accounts = new List<SavedAccount>();
        await using var connection = await OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.display_name, COALESCE(c.username, ''), COALESCE(c.password, '')
            FROM game_profiles p LEFT JOIN account_credentials c ON c.account_id = p.id
            ORDER BY p.created_utc, p.id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            accounts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return accounts;
    }

    /// <summary>一次写事务删除选中账号，外键级联删除凭据；空选择不打开数据库。</summary>
    public async Task DeleteAccountsAsync(IReadOnlyCollection<Guid> accountIds)
    {
        if (accountIds.Count == 0) return;
        await using var connection = await OpenAsync(default);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM game_profiles WHERE id = $id";
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in accountIds.Distinct())
        {
            idParameter.Value = id.ToString("D");
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }
    /// <summary>写事务持有 SQLite 写锁后检查账号，防止独立工具与面板同时创建重复记录。</summary>
    private static async Task EnsureUniqueUsernameAsync(SqliteConnection connection, SqliteTransaction transaction,
        string username, Guid accountId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT account_id FROM account_credentials WHERE username = $username AND account_id <> $id LIMIT 1";
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        if (await command.ExecuteScalarAsync() is string existingId)
            throw new AccountAlreadyExistsException(Guid.Parse(existingId));
    }
}
