using AFMediaBar.Classes.Models.Updates;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// "已下载并校验完成的更新"在后续检查中的保留判定。
///
/// 更新下载完成后状态进入 <see cref="UpdatePhase.Ready"/>，用户看到的是"点击重启安装"。但任何一次后续检查都会
/// 先把状态置为 Checking 再按清单结论改成 Available / UpToDate：只要清单源慢了一拍（备用源 jsDelivr 对分支文件
/// 有最长 12 小时缓存，主源失败时会回退到它）就会得出"已是最新"，把"重启安装"入口顶掉，用户于是以为更新没生效。
/// 待安装安装包仍然有效时，检查 MUST NOT 覆盖这个状态，因此本策略回答"这次检查要不要直接放过"。
/// Retention rule for an already downloaded and verified update across later checks.
///
/// A finished download puts the state into <see cref="UpdatePhase.Ready"/>, which is what shows "click to restart and
/// install". Any later check however first sets Checking and then rewrites the phase from the manifest's verdict: if the
/// manifest source lags by one release (the fallback source jsDelivr caches branch files for up to twelve hours and is
/// used when the primary fails), the verdict is "up to date" and the install entry disappears, which makes the user
/// think the update failed. While the pending installer is still valid a check MUST NOT overwrite that state, so this
/// policy answers "should this check be skipped".
/// </summary>
public static class UpdateReadyRetentionPolicy
{
    /// <summary>待安装安装包是否仍然可用：记录在、文件在、版本确实更新、没有被用户跳过。
    /// Whether the pending installer is still usable: the record exists, the file exists, the version really is newer,
    /// and the user has not skipped it.</summary>
    /// <param name="recordExists">待安装记录是否存在 / Whether the pending record exists.</param>
    /// <param name="fileExists">安装包文件是否还在 / Whether the installer file still exists.</param>
    /// <param name="versionIsNewer">记录版本是否高于当前运行版本 / Whether the recorded version is newer than the running one.</param>
    /// <param name="isSkipped">该版本是否被用户跳过 / Whether the user skipped that version.</param>
    public static bool IsPendingInstallerUsable(
        bool recordExists,
        bool fileExists,
        bool versionIsNewer,
        bool isSkipped) =>
        recordExists && fileExists && versionIsNewer && !isSkipped;

    /// <summary>这次检查是否应当保留"已就绪"状态并直接放过。/ Whether this check has to preserve the ready state and be skipped.</summary>
    /// <param name="phase">检查前的更新阶段 / Update phase before the check.</param>
    /// <param name="pendingInstallerUsable">待安装安装包是否仍然可用 / Whether the pending installer is still usable.</param>
    public static bool ShouldRetainReadyState(UpdatePhase phase, bool pendingInstallerUsable) =>
        phase == UpdatePhase.Ready && pendingInstallerUsable;
}
