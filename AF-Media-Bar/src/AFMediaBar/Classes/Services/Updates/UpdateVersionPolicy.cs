using AFMediaBar.Classes.Models.Updates;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// 版本号解析与比较的纯策略。
///
/// 发布侧写过 <c>1.1.1</c>、<c>v1.2</c> 和 <c>1.2.0.0</c>，程序集版本却总是四段数字，而 SourceLink 在 CI 构建里
/// 还会往信息版本追加提交号。把"哪些写法算同一个版本、谁更新"集中在这里，比较就永远不会受这些书写差异影响。
/// Pure policy for parsing and comparing version numbers.
///
/// The release side has written <c>1.1.1</c>, <c>v1.2</c> and <c>1.2.0.0</c>, the assembly version is always four
/// numeric parts, and SourceLink appends a commit id to the informational version in CI builds. Concentrating the
/// question of "which spellings mean the same version, and which one is newer" here keeps comparison immune to
/// those differences.
/// </summary>
public static class UpdateVersionPolicy
{
    /// <summary>显示版本号时保留的最少段数（主版本.次版本.修订版本）。/ Fewest segments kept when formatting a version (major.minor.build).</summary>
    public const int MinimumDisplaySegments = 3;

    /// <summary>
    /// 解析版本号。接受前后空白、前缀 <c>v</c>/<c>V</c>、1–4 段数字，以及 <c>-preview</c> 与 <c>+build</c> 后缀
    /// （后缀只被忽略，不参与比较）。
    /// Parses a version. Leading and trailing whitespace, a <c>v</c>/<c>V</c> prefix, one to four numeric segments,
    /// and <c>-preview</c> or <c>+build</c> suffixes are accepted; suffixes are ignored rather than compared.
    /// </summary>
    /// <param name="value">待解析文本。/ Text to parse.</param>
    /// <param name="version">解析结果；失败时为 <see cref="Version"/> 的全零值。/ Parsed version, or an all-zero value on failure.</param>
    /// <returns>是否解析成功。/ Whether parsing succeeded.</returns>
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        // 预发布与构建元数据只描述"哪一次构建"，不参与谁更新的判断。
        // Pre-release and build metadata describe which build this is, not which version is newer.
        var cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var segments = text.Split('.');
        if (segments.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0)
            {
                return false;
            }

            foreach (var character in segment)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }

            if (!int.TryParse(segment, out var number))
            {
                return false;
            }

            numbers[index] = number;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>
    /// 格式化为界面版本号：去掉尾部恒为零的段，至少保留主版本、次版本与修订版本。
    /// Formats a version for display: trailing all-zero segments are dropped down to major.minor.build.
    /// </summary>
    /// <param name="version">要格式化的版本；为 null 时返回空字符串。/ Version to format, or an empty string when null.</param>
    /// <returns>形如 <c>1.1.1</c> 的文本。/ Text such as <c>1.1.1</c>.</returns>
    public static string Format(Version? version)
    {
        if (version is null)
        {
            return string.Empty;
        }

        var segments = new List<int> { version.Major, version.Minor, version.Build, version.Revision };
        while (segments.Count > MinimumDisplaySegments && segments[^1] == 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return string.Join('.', segments);
    }

    /// <summary>
    /// 比较两个版本文本。任一文本无法解析时返回 false，调用方据此选择"不做判断"而不是猜测。
    /// Compares two version texts. Returns false when either cannot be parsed, which lets the caller choose "no
    /// judgement" instead of guessing.
    /// </summary>
    /// <param name="left">左值。/ Left value.</param>
    /// <param name="right">右值。/ Right value.</param>
    /// <param name="comparison">左值相对右值的大小关系。/ How the left value relates to the right one.</param>
    /// <returns>两侧都可解析时为 true。/ True when both sides parsed.</returns>
    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParse(left, out var leftVersion) || !TryParse(right, out var rightVersion))
        {
            return false;
        }

        comparison = leftVersion.CompareTo(rightVersion);
        return true;
    }

    /// <summary>两个文本是否描述同一个版本；任一无法解析时为 false。/ Whether both texts describe the same version; false when either is unparseable.</summary>
    /// <param name="left">左值。/ Left value.</param>
    /// <param name="right">右值。/ Right value.</param>
    public static bool IsSameVersion(string? left, string? right) =>
        TryCompare(left, right, out var comparison) && comparison == 0;

    /// <summary>
    /// 清单版本是否比当前版本更新。任一侧无法解析时返回 false：宁可提示"已是最新"，也不要因为一个写坏的
    /// 版本号给用户一个永远无法完成的更新。
    /// Whether the manifest version is newer than the running one. Unparseable input returns false: claiming "up
    /// to date" is preferable to offering an update that can never complete because of a malformed version number.
    /// </summary>
    /// <param name="currentVersion">当前运行的版本。/ Running version.</param>
    /// <param name="manifestVersion">清单声明的版本。/ Version declared by the manifest.</param>
    public static bool IsUpdateAvailable(string? currentVersion, string? manifestVersion) =>
        TryCompare(manifestVersion, currentVersion, out var comparison) && comparison > 0;

    /// <summary>
    /// 该更新是否为必须：清单显式标记，或当前版本低于清单的最低支持版本。
    /// Whether the update is required: the manifest marks it explicitly, or the running version is below the
    /// minimum supported version.
    /// </summary>
    /// <param name="currentVersion">当前运行的版本。/ Running version.</param>
    /// <param name="manifest">清单。/ Manifest.</param>
    public static bool IsMandatory(string? currentVersion, UpdateManifest? manifest)
    {
        if (manifest is null)
        {
            return false;
        }

        if (manifest.Mandatory)
        {
            return true;
        }

        return TryCompare(currentVersion, manifest.MinimumSupportedVersion, out var comparison) && comparison < 0;
    }
}
