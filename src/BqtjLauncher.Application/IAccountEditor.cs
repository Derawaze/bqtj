namespace BqtjLauncher.Application;

/// <summary>按本地账号 ID 原子编辑名称和凭据；空密码表示保留，清除必须显式指定。</summary>
public interface IAccountEditor
{
    Task<AccountCredential?> ReadCredentialAsync(Guid accountId);
    Task<Guid> CreateAsync(string displayName, string username, string password);
    Task SaveAsync(Guid accountId, string displayName, string username, string replacementPassword, bool clearCredential);
}

/// <summary>按需读取的本地凭据，不放入账号列表、日志或启动参数。</summary>
public sealed class AccountCredential(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;
    public override string ToString() => "[账号凭据已隐藏]";
}
