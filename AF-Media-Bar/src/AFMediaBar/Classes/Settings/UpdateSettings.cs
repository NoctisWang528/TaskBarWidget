namespace AFMediaBar.Classes.Settings;

/// <summary>
/// 更新下载器的用户设置。
///
/// 这里刻意不保存"上次下载到哪个文件"以外的运行态：待安装文件本身与它的哈希记在更新目录的
/// <c>pending.json</c> 里，因为那是文件系统的事实，而不是用户偏好。设置文件只保存用户能改的东西。
///
/// 也刻意没有"自动下载"开关：发现新版本只弹一次系统通知，下载与安装都由用户在"应用与关于"页显式触发，
/// 因此不需要一个会把 70 MB 流量变成默认行为的开关。
/// User settings for the update downloader.
///
/// No runtime state beyond the user's own choices is stored here: the downloaded installer and its hash live in
/// <c>pending.json</c> inside the update directory, because that is a fact about the file system rather than a
/// user preference, and the settings file only keeps what the user can change.
///
/// There is deliberately no "download automatically" switch either: discovering a newer version only raises a
/// single system notification, and both downloading and installing are started explicitly from the
/// "application and about" page, so no switch could turn 70 MB of traffic into default behaviour.
/// </summary>
/// <param name="AutoCheckEnabled">启动后是否自动检查版本清单。/ Whether to check the version manifest automatically after startup.</param>
/// <param name="SkippedVersion">用户跳过的版本号；为 null 时表示没有跳过任何版本。/ Version the user skipped, or null.</param>
/// <param name="LastCheckUtc">最近一次检查尝试的时间（成功与失败都记）。/ Time of the last check attempt, successful or not.</param>
/// <param name="LastCheckSucceeded">最近一次检查是否成功；为 null 表示从未检查过。/ Whether the last check succeeded, or null when never checked.</param>
public readonly record struct UpdateSettings(
    bool AutoCheckEnabled,
    string? SkippedVersion,
    DateTimeOffset? LastCheckUtc,
    bool? LastCheckSucceeded)
{
    /// <summary>跳过版本号的最大保存长度，避免设置文件里出现任意长文本。/ Maximum stored length of a skipped version string.</summary>
    public const int MaximumSkippedVersionLength = 32;

    /// <summary>默认设置：自动检查开启，未跳过任何版本，没有检查记录。/ Defaults: automatic checking is on, nothing is skipped, and no check has run yet.</summary>
    public static UpdateSettings Default { get; } = new(true, null, null, null);

    /// <summary>
    /// 归一化用户可编辑字段。
    ///
    /// 刻意不做时间比较：纯设置模型不允许依赖当前时间，否则同一份设置在不同时刻归一化出不同结果。
    /// "时间戳位于未来"由排期策略按"视为已到期"处理，而不是在写入时抹掉它。
    /// Normalizes the user-editable fields.
    ///
    /// Deliberately clock-free: a pure settings model must not depend on the current time, otherwise the same
    /// stored values normalize differently at different moments. A timestamp in the future is handled by the
    /// schedule policy as "due now" instead of being erased while writing.
    /// </summary>
    /// <returns>归一化后的设置。/ Normalized settings.</returns>
    public UpdateSettings Normalize()
    {
        var skipped = SkippedVersion?.Trim();
        if (string.IsNullOrEmpty(skipped))
        {
            skipped = null;
        }
        else if (skipped.Length > MaximumSkippedVersionLength)
        {
            skipped = skipped[..MaximumSkippedVersionLength];
        }

        return this with { SkippedVersion = skipped };
    }
}
