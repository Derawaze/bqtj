using System.IO;
using BqtjLauncher.Infrastructure;
using Microsoft.Data.Sqlite;

namespace BqtjLauncher.Application.Tests;

/// <summary>用独立虚构账号库验证导入续行、人工替换、唯一账号与可往返的导出。</summary>
public sealed class AccountTransferTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bqtj-transfer-test-{Guid.NewGuid():N}.db");
    private SqliteGameProfileRepository Repository => new(_path);
    private AccountTransferService Service => new(Repository);

    [Fact]
    public void ParseAcceptsRepeatedSpacesBlankLinesAndPreservesOriginalLineNumbers()
    {
        var (entries, issues) = AccountTransferFormat.Parse("\r\n名称1  fake-one   p!:#\r\n坏行 SECRET\n名称1\tfake-two\t虚构密码\n");
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries[0].LineNumber);
        Assert.Equal(4, entries[1].LineNumber);
        Assert.Equal("虚构密码", entries[1].Password);
        Assert.Equal(3, Assert.Single(issues).LineNumber);
        Assert.DoesNotContain("SECRET", issues[0].Message);
        Assert.DoesNotContain("p!:#", entries[0].ToString());
    }

    [Fact]
    public async Task DuplicateAndBadLineDoNotBlockFollowingAccountsOrOverwriteOldPassword()
    {
        await Repository.InitializeAsync();
        var oldId = await Repository.CreateAsync("原名称", "fake-existing", "old-password");
        var result = await Service.ImportAsync("同名 fake-one one\n新名称 fake-existing new-password\n坏行\n同名 fake-two two");
        Assert.Equal(2, result.AddedCount);
        Assert.Single(result.Issues);
        Assert.Equal(oldId, Assert.Single(result.Conflicts).Existing.Id);
        Assert.Equal("old-password", (await Repository.ReadCredentialAsync(oldId))!.Password);
        Assert.Equal("原名称", (await Repository.GetAsync(oldId))!.DisplayName);
        Assert.Equal(3, (await Repository.ListAsync()).Count);
    }

    [Fact]
    public async Task DuplicateWithinFileAlsoWaitsForReview()
    {
        await Repository.InitializeAsync();
        var result = await Service.ImportAsync("首条 fake-same first\n重复 fake-same second\n其他 fake-other other");
        Assert.Equal(2, result.AddedCount);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(2, conflict.Incoming.LineNumber);
        Assert.Equal("first", (await Repository.ReadCredentialAsync(conflict.Existing.Id))!.Password);
        await Service.ReplaceAsync(conflict, conflict.Incoming.DisplayName, conflict.Incoming.Username, conflict.Incoming.Password);
        Assert.Equal("second", (await Repository.ReadCredentialAsync(conflict.Existing.Id))!.Password);
        Assert.Equal(2, (await Repository.ListAsync()).Count);
    }

    [Fact]
    public async Task ReviewedReplacementPreservesIdentityAndLaunchMetadata()
    {
        await Repository.InitializeAsync();
        var id = await Repository.CreateAsync("原名称", "fake-account", "old-password");
        var original = (await Repository.GetAsync(id))!.MarkLaunched(DateTimeOffset.UtcNow);
        await Repository.UpsertAsync(original);
        var result = await Service.ImportAsync("新名称 fake-account new-password");
        await Service.ReplaceAsync(Assert.Single(result.Conflicts), "手动修改", "fake-account", "manual-password");
        var changed = (await Repository.GetAsync(id))!;
        Assert.Equal("手动修改", changed.DisplayName);
        Assert.Equal(original.BrowserProfileName, changed.BrowserProfileName);
        Assert.Equal(original.CreatedAtUtc, changed.CreatedAtUtc);
        Assert.Equal(original.LastLaunchedAtUtc, changed.LastLaunchedAtUtc);
        Assert.Equal("manual-password", (await Repository.ReadCredentialAsync(id))!.Password);
    }

    [Fact]
    public async Task CreateAndEditRejectDuplicateUsernameWithoutChangingEitherRecord()
    {
        await Repository.InitializeAsync();
        var a = await Repository.CreateAsync("同名", "fake-a", "password-a");
        var b = await Repository.CreateAsync("同名", "fake-b", "password-b");
        var duplicate = await Assert.ThrowsAsync<AccountAlreadyExistsException>(() => Repository.CreateAsync("第三条", " fake-a ", "third"));
        Assert.Equal(a, duplicate.AccountId);
        await Assert.ThrowsAsync<AccountAlreadyExistsException>(() => Repository.SaveAsync(b, "不应覆盖", "fake-a", "wrong", false));
        Assert.Equal(2, (await Repository.ListAsync()).Count);
        Assert.Equal("同名", (await Repository.GetAsync(b))!.DisplayName);
        Assert.Equal("password-b", (await Repository.ReadCredentialAsync(b))!.Password);
    }

    [Fact]
    public async Task ConcurrentIndependentEditorsCreateOnlyOneCopy()
    {
        await Repository.InitializeAsync();
        async Task<bool> CreateOneAsync(string name)
        {
            try { await Repository.CreateAsync(name, "fake-shared", "synthetic-password"); return true; }
            catch (AccountAlreadyExistsException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => CreateOneAsync("面板")), Task.Run(() => CreateOneAsync("工具")));
        Assert.Single(results, created => created);
        Assert.Single(await Repository.ListAsync());
    }

    [Fact]
    public async Task ExportRoundTripsCredentialsAndOmitsRecordsWithoutCredentials()
    {
        await Repository.InitializeAsync();
        await Repository.CreateAsync("同名", "fake-one", "p!:#");
        await Repository.CreateAsync("同名", "fake-two", "虚构密码");
        await Repository.CreateAsync("手动登录", "", "");
        var saved = await Service.ListAsync();
        var text = AccountTransferFormat.Export(saved);
        var (entries, issues) = AccountTransferFormat.Parse(text);
        Assert.Empty(issues);
        Assert.Equal(saved.Select(account => (account.DisplayName, account.Username, account.Password)),
            entries.Select(entry => (entry.DisplayName, entry.Username, entry.Password)));
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain("虚构密码", saved[1].ToString());
    }

    [Fact]
    public async Task InvalidManualReplacementAndUnrepresentableExportDoNotChangeSavedData()
    {
        await Repository.InitializeAsync();
        var id = await Repository.CreateAsync("含 空格", "fake-account", "old-password");
        var conflict = Assert.Single((await Service.ImportAsync("新名称 fake-account new-password")).Conflicts);
        await Assert.ThrowsAsync<ArgumentException>(() => Service.ReplaceAsync(conflict, "手动名称", "fake-account", ""));
        var saved = await Service.ListAsync();
        Assert.Throws<ArgumentException>(() => AccountTransferFormat.Export(saved));
        Assert.Equal("old-password", (await Repository.ReadCredentialAsync(id))!.Password);
    }

    [Fact]
    public async Task LegacyDuplicatesRemainReadableAndAreNotSilentlyMerged()
    {
        await Repository.InitializeAsync();
        var a = await Repository.CreateAsync("历史A", "fake-legacy", "a");
        var b = await Repository.CreateAsync("历史B", "", "");
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO account_credentials(account_id,username,password) VALUES($id,'fake-legacy','b')";
            command.Parameters.AddWithValue("$id", b.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        await Repository.InitializeAsync();
        Assert.Equal(2, (await Service.ListAsync()).Count);
        await Assert.ThrowsAsync<AccountAlreadyExistsException>(() => Repository.CreateAsync("新记录", "fake-legacy", "c"));
        Assert.Equal("a", (await Repository.ReadCredentialAsync(a))!.Password);
        Assert.Equal("b", (await Repository.ReadCredentialAsync(b))!.Password);
    }

    [Fact]
    public async Task ManagementListIncludesManualAccountsButExportStillOmitsThem()
    {
        await Repository.InitializeAsync();
        var manual = await Repository.CreateAsync("手动登录", "", "");
        await Repository.CreateAsync("自动登录", "fake-account", "synthetic-password");
        var accounts = await Service.ListAccountsAsync();
        Assert.Equal(2, accounts.Count);
        var row = Assert.Single(accounts, account => account.Id == manual);
        Assert.Equal("", row.Username);
        Assert.Equal("", row.Password);
        Assert.Single(await Service.ListAsync());
        var changed = await Service.SaveManagedAccountAsync(row with { DisplayName = "新的手动名称" });
        Assert.Equal("新的手动名称", changed.DisplayName);
        Assert.Null(await Repository.ReadCredentialAsync(manual));
    }

    [Fact]
    public async Task InlineEditsKeepIdentityAndReturnRetainedPassword()
    {
        await Repository.InitializeAsync();
        var id = await Repository.CreateAsync("原名称", "fake-one", "old-password");
        var original = (await Repository.GetAsync(id))!.MarkLaunched(DateTimeOffset.UtcNow);
        await Repository.UpsertAsync(original);
        var row = Assert.Single(await Service.ListAccountsAsync());
        var changed = await Service.SaveManagedAccountAsync(row with { DisplayName = "新名称", Username = "fake-changed", Password = "" });
        Assert.Equal("old-password", changed.Password);
        var profile = (await Repository.GetAsync(id))!;
        Assert.Equal(original.BrowserProfileName, profile.BrowserProfileName);
        Assert.Equal(original.CreatedAtUtc, profile.CreatedAtUtc);
        Assert.Equal(original.LastLaunchedAtUtc, profile.LastLaunchedAtUtc);
        await Service.SaveManagedAccountAsync(changed with { Password = "new-password" });
        Assert.Equal("new-password", (await Repository.ReadCredentialAsync(id))!.Password);
    }

    [Fact]
    public async Task BatchDeleteRemovesOnlySelectedRecordsAndTheirCredentials()
    {
        await Repository.InitializeAsync();
        var a = await Repository.CreateAsync("A", "fake-a", "a");
        var b = await Repository.CreateAsync("B", "fake-b", "b");
        var manual = await Repository.CreateAsync("手动", "", "");
        await Service.DeleteAsync([a, manual, a, Guid.NewGuid()]);
        Assert.Equal(b, Assert.Single(await Repository.ListAsync()).Id);
        Assert.Null(await Repository.ReadCredentialAsync(a));
        Assert.Equal("b", (await Repository.ReadCredentialAsync(b))!.Password);
        await Service.DeleteAsync([]);
        Assert.Single(await Service.ListAccountsAsync());
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }
}
