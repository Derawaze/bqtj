using System.IO;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;
using Microsoft.Data.Sqlite;

namespace BqtjLauncher.Application.Tests;

/// <summary>使用独立临时数据库验证真实 SQLite 凭据生命周期，不使用平台账号。</summary>
public sealed class AccountEditorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bqtj-credential-test-{Guid.NewGuid():N}.db");
    private SqliteGameProfileRepository Repository => new(_path);

    private async Task<GameProfile> CreateAsync(string name)
    {
        await Repository.InitializeAsync();
        var profile = GameProfile.Create(name, DateTimeOffset.UtcNow);
        await Repository.UpsertAsync(profile);
        return profile;
    }

    [Fact]
    public async Task CreateWithCredentialIsAtomicAndPreservesBothFields()
    {
        await Repository.InitializeAsync();
        var id = await Repository.CreateAsync("一起创建", "synthetic", "password");
        Assert.Equal("一起创建", (await Repository.GetAsync(id))!.DisplayName);
        Assert.Equal("password", (await Repository.ReadCredentialAsync(id))!.Password);
        await Assert.ThrowsAsync<ArgumentException>(() => Repository.CreateAsync("失败", "synthetic", ""));
        Assert.Single(await Repository.ListAsync());
        var empty = await Repository.CreateAsync("不保存密码", "", "");
        Assert.Null(await Repository.ReadCredentialAsync(empty));
    }

    [Fact]
    public async Task ReopenPreservesExactPlaintextAndSeparatesAccounts()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        const string password = "虚构 ' \" \\ 密码\n空格 ";
        await Repository.SaveAsync(a.Id, "A新名称", "synthetic-a", password, false);
        await Repository.SaveAsync(b.Id, "B", "synthetic-b", "test-b", false);
        Assert.Equal(password, (await Repository.ReadCredentialAsync(a.Id))!.Password);
        Assert.Equal("synthetic-b", (await Repository.ReadCredentialAsync(b.Id))!.Username);
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password FROM account_credentials WHERE account_id=$id";
        command.Parameters.AddWithValue("$id", a.Id.ToString("D"));
        Assert.Equal(password, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PreserveReplaceClearAndDeleteAreIndependent()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        await Repository.SaveAsync(a.Id, "A", "test-a", "original", false);
        await Repository.SaveAsync(b.Id, "B", "test-b", "other", false);
        await Repository.SaveAsync(a.Id, "重命名", "changed", "", false);
        Assert.Equal("original", (await Repository.ReadCredentialAsync(a.Id))!.Password);
        await Repository.SaveAsync(a.Id, "重命名", "changed", "replacement", false);
        Assert.Equal("replacement", (await Repository.ReadCredentialAsync(a.Id))!.Password);
        await Repository.SaveAsync(a.Id, "重命名", "", "", true);
        Assert.Null(await Repository.ReadCredentialAsync(a.Id));
        Assert.NotNull(await Repository.ReadCredentialAsync(b.Id));
        await Repository.DeleteAsync(b.Id);
        Assert.Null(await Repository.ReadCredentialAsync(b.Id));
    }

    [Fact]
    public async Task InvalidCredentialRollsBackNameAndPreservesLaunchTime()
    {
        var a = await CreateAsync("A");
        var launched = a.MarkLaunched(DateTimeOffset.UtcNow);
        await Repository.UpsertAsync(launched);
        await Assert.ThrowsAsync<ArgumentException>(() => Repository.SaveAsync(a.Id, "不应写入", "test", "", false));
        Assert.Equal("A", (await Repository.GetAsync(a.Id))!.DisplayName);
        await Repository.SaveAsync(a.Id, "新名称", "test", "password", false);
        Assert.Equal(launched.LastLaunchedAtUtc, (await Repository.GetAsync(a.Id))!.LastLaunchedAtUtc);
    }

    [Fact]
    public async Task ExistingProfileSurvivesSchemaUpgradeAndRepeatedInitialization()
    {
        var a = await CreateAsync("旧账号");
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE account_credentials";
            await command.ExecuteNonQueryAsync();
        }
        await Repository.InitializeAsync();
        await Repository.InitializeAsync();
        Assert.Equal("旧账号", (await Repository.GetAsync(a.Id))!.DisplayName);
        Assert.Null(await Repository.ReadCredentialAsync(a.Id));
        await Assert.ThrowsAsync<BqtjLauncher.Application.ProfileNotFoundException>(() => Repository.SaveAsync(Guid.NewGuid(), "不存在", "test", "password", false));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
