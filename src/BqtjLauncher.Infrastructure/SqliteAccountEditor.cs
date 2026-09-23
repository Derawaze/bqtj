using BqtjLauncher.Application;
using BqtjLauncher.Domain;
using Microsoft.Data.Sqlite;

namespace BqtjLauncher.Infrastructure;

/// <summary>按用户选择明文保存凭据，名称与凭据同事务提交，不覆盖最近启动时间。</summary>
public sealed partial class SqliteGameProfileRepository
{
    /// <summary>新增和凭据写入同事务，取消表单或写入失败不留下半条账号记录。</summary>
    public async Task<Guid> CreateAsync(string displayName, string username, string password)
    {
        var profile = GameProfile.Create(displayName, DateTimeOffset.UtcNow);
        username = username.Trim();
        if (username.Length > 256 || password.Length > 1024 || (username.Length == 0) != (password.Length == 0))
            throw new ArgumentException("请同时填写账号和密码，或同时留空。");
        await using var connection = await OpenAsync(default);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO game_profiles(id,display_name,browser_profile_name,created_utc) VALUES($id,$name,$browser,$created)";
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$browser", profile.BrowserProfileName);
        command.Parameters.AddWithValue("$created", profile.CreatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
        if (username.Length != 0)
        {
            command.CommandText = "INSERT INTO account_credentials(account_id,username,password) VALUES($id,$username,$password)";
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$password", password);
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
        return profile.Id;
    }

    public async Task<AccountCredential?> ReadCredentialAsync(Guid accountId)
    {
        await using var connection = await OpenAsync(default);
        return await ReadCredentialAsync(connection, null, accountId);
    }

    private static async Task<AccountCredential?> ReadCredentialAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid accountId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT username, password FROM account_credentials WHERE account_id = $id";
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new AccountCredential(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async Task SaveAsync(Guid accountId, string displayName, string username, string replacementPassword, bool clearCredential)
    {
        var normalizedName = GameProfile.Create(displayName, DateTimeOffset.UtcNow).DisplayName;
        username = username.Trim();
        if (username.Length > 256 || replacementPassword.Length > 1024)
            throw new ArgumentException("账号或密码长度超出限制。");
        await using var connection = await OpenAsync(default);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE game_profiles SET display_name = $name WHERE id = $id";
        command.Parameters.AddWithValue("$id", accountId.ToString("D"));
        command.Parameters.AddWithValue("$name", normalizedName);
        if (await command.ExecuteNonQueryAsync() != 1) throw new ProfileNotFoundException(accountId);
        if (clearCredential)
        {
            command.CommandText = "DELETE FROM account_credentials WHERE account_id = $id";
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var previous = replacementPassword.Length == 0
                ? await ReadCredentialAsync(connection, transaction, accountId) : null;
            if (username.Length != 0 || replacementPassword.Length != 0 || previous is not null)
            {
                // 空密码保留原值；清除必须显式选择，防止误删已有凭据。
                var password = replacementPassword.Length == 0 ? previous?.Password : replacementPassword;
                if (username.Length == 0 || string.IsNullOrEmpty(password))
                    throw new ArgumentException("请填写4399账号和密码；删除保存信息请勾选清除。");
                command.CommandText = "INSERT INTO account_credentials(account_id, username, password) VALUES($id, $username, $password) ON CONFLICT(account_id) DO UPDATE SET username=excluded.username, password=excluded.password";
                command.Parameters.AddWithValue("$username", username);
                command.Parameters.AddWithValue("$password", password);
                await command.ExecuteNonQueryAsync();
            }
        }
        transaction.Commit();
    }
}
