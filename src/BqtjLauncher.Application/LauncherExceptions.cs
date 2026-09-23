namespace BqtjLauncher.Application;

public sealed class ProfileNotFoundException(Guid profileId)
    : InvalidOperationException($"找不到游戏档案 {profileId}。");

public sealed class ProfileAlreadyRunningException(Guid profileId)
    : InvalidOperationException($"游戏档案 {profileId} 已经在运行。");

public sealed class SessionLimitReachedException(int limit)
    : InvalidOperationException($"已达到最多 {limit} 个并发游戏会话的限制。");

public sealed class ProfileInUseException(Guid profileId)
    : InvalidOperationException($"游戏档案 {profileId} 正在运行，不能删除。");
