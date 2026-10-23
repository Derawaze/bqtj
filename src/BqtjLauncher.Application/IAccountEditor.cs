namespace BqtjLauncher.Application;

/// <summary>按本地账号 ID 原子编辑名称、总统权益和凭据；空密码表示保留，清除必须显式指定。</summary>
public interface IAccountEditor
{
    Task<AccountCredential?> ReadCredentialAsync(Guid accountId);
    Task<Guid> CreateAsync(string displayName, string username, string password, bool hasPresident = false);
    /// <summary>未传权益时保留原值，兼容三字段导入及重复账号审核。</summary>
    Task SaveAsync(Guid accountId, string displayName, string username, string replacementPassword, bool clearCredential, bool? hasPresident = null);
}

/// <summary>按需读取的本地凭据，不放入账号列表、日志或启动参数。</summary>
public sealed class AccountCredential(string username, string password)
{
    public string Username { get; } = username;
    public string Password { get; } = password;
    public override string ToString() => "[账号凭据已隐藏]";
}
