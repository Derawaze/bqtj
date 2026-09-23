namespace BqtjLauncher.Runtime.Flash;

/// <summary>
/// 定义账号与 Windows AppContainer profile 之间的稳定映射。
/// 映射只使用不可变账号 ID，不受账号显示名称或数据库位置影响。
/// </summary>
public static class AccountIsolationIdentity
{
    private const string MonikerPrefix = "Derawaze.Bqtj.Account.";

    public static string CreateMoniker(Guid accountId)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("账号 ID 不能为空。", nameof(accountId));
        }

        return $"{MonikerPrefix}{accountId:N}";
    }
}
