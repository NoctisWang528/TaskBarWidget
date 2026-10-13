// 描述可选媒体来源与其可选的 SMTC 控制会话；不持有原生会话或进程资源。
namespace AFMediaBar.Classes.Models;

/// <summary>供来源选择使用的不可变候选；内存来源可以没有 SMTC 会话。 / Immutable selection candidate with an optional SMTC session.</summary>
/// <param name="Key">来源选择键。</param>
/// <param name="SourceId">应用来源标识。</param>
/// <param name="IsPlaying">当前是否正在播放。</param>
/// <param name="SessionKey">可选的 SMTC 会话键，仅用于关联同来源控制。</param>
/// <param name="IsRecovering">是否正在等待短暂读取失败恢复。</param>
public sealed record MediaSourceCandidate(
    string Key, string SourceId, bool IsPlaying, string? SessionKey, bool IsRecovering = false);
