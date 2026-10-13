using AFMediaBar.Classes.Models.Credits;

namespace AFMediaBar.Classes.Models.Credits;

/// <summary>
/// 一份名单快照：关于页要展示的全部内容，加上它是从哪来、什么时候来的。
/// A name-list snapshot: everything the about page shows, plus where it came from and when.
/// </summary>
/// <param name="Contributors">贡献者。/ Contributors.</param>
/// <param name="Sponsors">赞助者。/ Sponsors.</param>
/// <param name="FromCache">是否来自本地缓存。/ Whether this came from the local cache.</param>
/// <param name="FetchedUtc">数据写入时间；从未取到过时为 null。/ When the data was written, or null when it has never been fetched.</param>
/// <param name="FailureReason">失败原因；两份名单都取到时为 null。/ The failure reason, or null when both lists arrived.</param>
/// <param name="IsLoading">是否正在取。/ Whether a fetch is in flight.</param>
/// <param name="ContributorsLoaded">
/// 开发人员名单是否**成功取到过**（空列表也算成功）。
/// 它与"有没有名字"是两件事：界面据此区分"这份名单是空的"与"这份名单没取到"——前者该显示一行"暂无"，后者该显示失败原因。
/// Whether the developer list has **ever been fetched successfully**, an empty list included.
/// That is not the same question as "are there any names": the interface uses it to tell "this list is empty" from "this list could not be loaded" — the former
/// deserves a "none yet" line and the latter the failure reason.
/// </param>
/// <param name="SponsorsLoaded">赞助者名单是否成功取到过。/ Whether the sponsor list has ever been fetched successfully.</param>
public sealed record CreditsSnapshot(
    IReadOnlyList<ContributorInfo> Contributors,
    IReadOnlyList<SponsorInfo> Sponsors,
    bool FromCache,
    DateTimeOffset? FetchedUtc,
    string? FailureReason,
    bool IsLoading,
    bool ContributorsLoaded = false,
    bool SponsorsLoaded = false)
{
    /// <summary>空快照：什么都没取到时使用。/ The empty snapshot, used when nothing has been fetched.</summary>
    public static CreditsSnapshot Empty { get; } =
        new([], [], FromCache: false, FetchedUtc: null, FailureReason: null, IsLoading: false);

    /// <summary>是否拿到了任何可以展示的内容。/ Whether anything showable arrived.</summary>
    public bool HasAny => Contributors.Count > 0 || Sponsors.Count > 0;

    /// <summary>是否只有一部分名单取到了（用于把失败原因显示成"部分失败"）。/ Whether only part of the lists arrived, which turns the failure reason into a "partial" notice.</summary>
    public bool IsPartial => FailureReason is not null && (ContributorsLoaded || SponsorsLoaded);
}
