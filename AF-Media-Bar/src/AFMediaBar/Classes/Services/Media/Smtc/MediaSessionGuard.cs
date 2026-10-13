// Checks borrowed SMTC session references; owns no sessions or native resources.
using static WindowsMediaController.MediaManager;

namespace AFMediaBar.Classes.Services.Media.Smtc;

/// <summary>
/// 统一判定第三方媒体会话是否仍可读取，并提供不依赖 <c>ControlSession</c> 的稳定来源标识。
/// 第三方库在会话关闭时先从 <c>CurrentMediaSessions</c> 移除该会话，再把 <c>ControlSession</c> 置空；
/// 而目录发布的稳定数组只是引用拷贝，取消订阅会在 WinRT 事件线程发生，因此被拷贝出来的实例可能在读取前变成空引用。
/// 所有消费者必须先用本类型判定可用性，并且只使用构造时冻结的 <see cref="MediaSession.Id"/> 作为会话与来源标识。
/// Resolves whether a third-party media session is still readable and exposes a stable source identifier that does not
/// depend on <c>ControlSession</c>. The library removes a session from <c>CurrentMediaSessions</c> and then clears its
/// <c>ControlSession</c>, while the stable array is only a reference copy; a copied instance can therefore become a null
/// reference before it is read. Every consumer must check liveness first and must use the immutable <c>Id</c> for identity.
/// </summary>
internal static class MediaSessionGuard
{
    /// <summary>
    /// 判定会话是否仍持有可用的 SMTC 控制会话；已关闭的会话返回 <see langword="false"/>。
    /// Returns whether the session still holds a usable SMTC control session; a closed session returns <see langword="false"/>.
    /// </summary>
    public static bool IsUsable(MediaSession? session) => session?.ControlSession is not null;

    /// <summary>
    /// 返回与 SMTC 来源标识等价的稳定字符串；<c>Id</c> 由 <c>SourceAppUserModelId</c> 在构造时写入且不再变更，
    /// 因此在会话关闭竞态下仍可安全读取，可以替代 <c>ControlSession.SourceAppUserModelId</c>。
    /// Returns the stable string equivalent to the SMTC source identifier; <c>Id</c> is written from
    /// <c>SourceAppUserModelId</c> at construction and never changes, so it stays readable while a session is closing and
    /// replaces <c>ControlSession.SourceAppUserModelId</c>.
    /// </summary>
    public static string GetSourceId(MediaSession session) => session.Id ?? string.Empty;
}
