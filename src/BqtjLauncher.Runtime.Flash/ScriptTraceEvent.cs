using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>脚本运行的结构化诊断；只含会话、几何、固定菜单词和错误类型，不携带账号或方案原文。</summary>
public sealed record ScriptTraceEvent(DateTimeOffset At, Guid Run, Guid Session, string Event, string Stage,
    long Snapshot, long FrameAgeMs, ScriptRect? Region = null, int? X = null, int? Y = null,
    long? DurationMs = null, string? Result = null, IReadOnlyList<ScriptTraceText>? Menu = null, int? TextCount = null, int? Attempt = null);

/// <summary>经固定词表筛选的菜单识别，保留置信度和游戏坐标用于复盘，不记录任意OCR正文。</summary>
public sealed record ScriptTraceText(string Text, ScriptRect Bounds, double Confidence);
