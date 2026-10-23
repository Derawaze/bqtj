using BqtjLauncher.Application;

namespace BqtjLauncher.Infrastructure;

/// <summary>脚本选择列表读取名称与用户名辅助区分同名账号，不读取密码。</summary>
public sealed partial class SqliteGameProfileRepository : IScriptAccounts
{
    public async Task<IReadOnlyList<ScriptAccount>> ListScriptAccountsAsync()
    {
        var result = new List<ScriptAccount>();
        await using var connection = await OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.display_name, COALESCE(c.username, ''), p.has_president
            FROM game_profiles p LEFT JOIN account_credentials c ON c.account_id = p.id
            ORDER BY p.created_utc, p.id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
        return result;
    }
}
