using System.Text;

namespace BqtjLauncher.Application;

/// <summary>批量工具使用的凭据快照；只在工具内处理，不进入日志。</summary>
public sealed record SavedAccount(Guid Id, string DisplayName, string Username, string Password, bool HasPresident = false)
{
    public override string ToString() => "[账号凭据已隐藏]";
}

/// <summary>复用原子账号编辑，并提供批量工具所需的已保存凭据清单。</summary>
public interface IAccountTransferStore : IAccountEditor
{
    Task<IReadOnlyList<SavedAccount>> ListSavedAccountsAsync();
    Task<IReadOnlyList<SavedAccount>> ListAccountsAsync();
    Task DeleteAccountsAsync(IReadOnlyCollection<Guid> accountIds);
}

/// <summary>账号重复时携带已有记录 ID，供人工审核定位；异常正文不包含凭据。</summary>
public sealed class AccountAlreadyExistsException(Guid accountId)
    : InvalidOperationException("该4399账号已存在，请编辑原记录或使用批量工具审核。")
{
    public Guid AccountId { get; } = accountId;
}

/// <summary>每行导入数据保留原行号，错误和重复项可以准确定位。</summary>
public sealed record AccountImportEntry(int LineNumber, string DisplayName, string Username, string Password)
{
    public override string ToString() => $"[第 {LineNumber} 行凭据已隐藏]";
}

/// <summary>不回显原始凭据的行号与错误说明。</summary>
public sealed record AccountImportIssue(int LineNumber, string Message);
/// <summary>待人工审核的新数据与原记录快照。</summary>
public sealed record AccountImportConflict(AccountImportEntry Incoming, SavedAccount Existing);
/// <summary>已完成的新增数量、未覆盖的重复项以及无效行。</summary>
public sealed record AccountImportResult(int AddedCount, IReadOnlyList<AccountImportConflict> Conflicts,
    IReadOnlyList<AccountImportIssue> Issues);

/// <summary>解析和输出“名称 账号 密码”；空行忽略，错误仅报告行号，不回显原始密码。</summary>
public static class AccountTransferFormat
{
    /// <summary>逐行解析三项字段，保留错误行号，合法行不因其他行错误而丢弃。</summary>
    public static (IReadOnlyList<AccountImportEntry> Entries, IReadOnlyList<AccountImportIssue> Issues) Parse(string text)
    {
        var entries = new List<AccountImportEntry>();
        var issues = new List<AccountImportIssue>();
        using var reader = new StringReader(text);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            // 连续空格或制表符作为同一个分隔，CRLF/LF 由逐行读取统一处理。
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3)
            {
                issues.Add(new(lineNumber, "需要三项：名称 账号 密码。"));
                continue;
            }
            if (fields[0].Length > 40 || fields[1].Length > 256 || fields[2].Length > 1024)
            {
                issues.Add(new(lineNumber, "名称最多40字，账号最多256字，密码最多1024字。"));
                continue;
            }
            entries.Add(new(lineNumber, fields[0], fields[1], fields[2]));
        }
        return (entries, issues);
    }

    /// <summary>导出必须可按同一格式重新导入；旧记录含空白时明确交由用户修改，不静默丢失字段。</summary>
    public static string Export(IReadOnlyList<SavedAccount> accounts)
    {
        var text = new StringBuilder();
        foreach (var account in accounts)
        {
            if (new[] { account.DisplayName, account.Username, account.Password }
                .Any(value => value.Length == 0 || value.Any(char.IsWhiteSpace)))
                throw new ArgumentException("存在名称、账号或密码含空白的记录，请先编辑后再导出。未保存账号密码的记录不参与导出。");
            text.Append(account.DisplayName).Append(' ').Append(account.Username).Append(' ')
                .Append(account.Password).AppendLine();
        }
        return text.ToString();
    }
}

/// <summary>先完成所有非重复导入，再返回审核队列；只有用户确认的重复项才更新原记录。</summary>
public sealed class AccountTransferService(IAccountTransferStore store)
{
    public Task<IReadOnlyList<SavedAccount>> ListAsync() => store.ListSavedAccountsAsync();

    /// <summary>管理列表包含无凭据的手动登录记录，导出仍使用已保存凭据清单。</summary>
    public Task<IReadOnlyList<SavedAccount>> ListAccountsAsync() => store.ListAccountsAsync();

    /// <summary>原位编辑复用原子保存与空密码保留规则，返回实际凭据，避免界面误显示空密码。</summary>
    public async Task<SavedAccount> SaveManagedAccountAsync(SavedAccount account)
    {
        await store.SaveAsync(account.Id, account.DisplayName, account.Username, account.Password, clearCredential: false, hasPresident: account.HasPresident);
        var credential = await store.ReadCredentialAsync(account.Id);
        return account with
        {
            DisplayName = account.DisplayName.Trim(),
            Username = credential?.Username ?? string.Empty,
            Password = credential?.Password ?? string.Empty,
        };
    }

    /// <summary>只删除用户选择的稳定 ID，同事务清除凭据，其他账号不受影响。</summary>
    public Task DeleteAsync(IReadOnlyCollection<Guid> accountIds) => store.DeleteAccountsAsync(accountIds);
    /// <summary>新记录立即原子保存，已有账号只收集到审核队列，不在这里覆盖密码。</summary>
    public async Task<AccountImportResult> ImportAsync(string text)
    {
        var (entries, parseIssues) = AccountTransferFormat.Parse(text);
        var issues = parseIssues.ToList();
        var conflicts = new List<AccountImportConflict>();
        var known = (await store.ListSavedAccountsAsync()).ToDictionary(account => account.Id);
        var added = 0;
        foreach (var entry in entries)
        {
            try
            {
                var id = await store.CreateAsync(entry.DisplayName, entry.Username, entry.Password);
                known[id] = new(id, entry.DisplayName, entry.Username, entry.Password);
                added++;
            }
            catch (AccountAlreadyExistsException duplicate)
            {
                if (!known.TryGetValue(duplicate.AccountId, out var existing))
                {
                    // 另一面板可能刚创建该账号；重新读取，而不是把重复记录作为新账号写入。
                    known = (await store.ListSavedAccountsAsync()).ToDictionary(account => account.Id);
                    if (!known.TryGetValue(duplicate.AccountId, out existing))
                    {
                        issues.Add(new(entry.LineNumber, "重复记录已变化，请重新导入这一行。"));
                        continue;
                    }
                }
                conflicts.Add(new(entry, existing));
            }
            catch (ArgumentException)
            {
                issues.Add(new(entry.LineNumber, "账号内容无效，请检查该行后重新导入。"));
            }
        }
        return new(added, conflicts, issues);
    }

    /// <summary>替换沿用原 ID、浏览器标识和启动时间；允许用户在审核时修改三项内容。</summary>
    public Task ReplaceAsync(AccountImportConflict conflict, string displayName, string username, string password)
    {
        if (new[] { displayName, username, password }.Any(value => string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) ||
            displayName.Length > 40 || username.Length > 256 || password.Length > 1024)
            throw new ArgumentException("请完整填写名称、账号和密码，三项均不能包含空白。");
        return store.SaveAsync(conflict.Existing.Id, displayName, username, password, clearCredential: false);
    }
}
