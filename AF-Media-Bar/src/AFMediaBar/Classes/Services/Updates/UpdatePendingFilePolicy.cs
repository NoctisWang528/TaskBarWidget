using AFMediaBar.Classes.Models.Updates;

namespace AFMediaBar.Classes.Services.Updates;

/// <summary>
/// 对已下载安装包应当执行的动作。
/// Action to take for an already downloaded installer.
/// </summary>
public enum UpdatePendingFileAction
{
    /// <summary>文件不存在或记录缺失，需要下载。/ The file or its record is missing, so it must be downloaded.</summary>
    Download = 0,

    /// <summary>文件未被改动过，可以直接复用，无需重新读取 70 MB。/ The file is untouched and can be reused without re-reading 70 MB.</summary>
    Reuse = 1,

    /// <summary>文件大小与记录一致但时间戳变了，需要重新校验后再决定。/ Size still matches but the timestamp changed, so it must be verified again.</summary>
    VerifyAgain = 2,

    /// <summary>文件与清单不符，必须丢弃并重新下载。/ The file disagrees with the manifest and must be discarded.</summary>
    Discard = 3
}

/// <summary>
/// 更新目录里 <c>pending.json</c> 的内容：已下载安装包的身份。
///
/// 记录时间戳是为了让"退出时安装"这条路径不必每次重算 70 MB 的哈希：只要大小、哈希与时间戳三样都没变，
/// 就认定文件仍是当初通过校验的那一个；任何一样变了都退回重新校验，而不是相信它。
/// Contents of <c>pending.json</c> in the update directory: the identity of a downloaded installer.
///
/// The timestamp is recorded so the "install on exit" path does not re-hash 70 MB every time: when size, hash and
/// timestamp all still match, the file is the one that passed verification; when any of them differs the file is
/// verified again instead of trusted.
/// </summary>
/// <param name="Path">安装包在本机的位置。/ Local installer path.</param>
/// <param name="Version">该安装包的版本。/ Version of the installer.</param>
/// <param name="Sha256">校验通过时的 SHA-256。/ SHA-256 the file passed verification with.</param>
/// <param name="Size">校验通过时的字节数。/ Size in bytes at verification time.</param>
/// <param name="ModifiedUtc">校验通过时的文件写入时间（UTC）。/ File write time in UTC at verification time.</param>
public sealed record UpdatePendingFileRecord(
    string Path,
    string Version,
    string Sha256,
    long Size,
    DateTimeOffset ModifiedUtc);

/// <summary>
/// 已下载安装包的复用策略。
/// Reuse policy for a downloaded installer.
/// </summary>
public static class UpdatePendingFilePolicy
{
    /// <summary>
    /// 决定复用、重新校验还是丢弃。
    ///
    /// 清单长度不参与判断：完整性只由 SHA-256 决定，而"本机这份文件还是当初校验过的那一份吗"由记录里的长度与
    /// 写入时间回答——它们是本机文件的事实，与清单是否声明长度无关。
    /// Decides whether to reuse, verify again or discard.
    ///
    /// The manifest length plays no part: integrity is decided by SHA-256 alone, while "is the local file still the
    /// one that passed verification" is answered by the recorded length and write time, which are facts about the
    /// local file rather than anything the manifest declares.
    /// </summary>
    /// <param name="record">上次成功校验留下的记录；没有记录时为 null。/ Record left by the last successful verification, or null.</param>
    /// <param name="asset">清单里的安装包。/ Package from the manifest.</param>
    /// <param name="fileExists">文件当前是否存在。/ Whether the file exists right now.</param>
    /// <param name="actualSize">文件当前的实际字节数。/ Actual size of the file right now.</param>
    /// <param name="actualModifiedUtc">文件当前的写入时间（UTC）。/ Actual write time in UTC right now.</param>
    public static UpdatePendingFileAction Decide(
        UpdatePendingFileRecord? record,
        UpdatePackageAsset asset,
        bool fileExists,
        long actualSize,
        DateTimeOffset actualModifiedUtc)
    {
        if (!fileExists || record is null)
        {
            return UpdatePendingFileAction.Download;
        }

        // 记录与清单指的不是同一个文件：无论本机那份是什么，都不能拿它去安装。
        // The record and the manifest disagree about which file this is, so whatever is on disk must not be installed.
        if (!string.Equals(record.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePendingFileAction.Discard;
        }

        // 本机文件被换过：先重新哈希确认，而不是直接相信它。
        // The local file changed: it is re-hashed rather than trusted.
        if (record.Size != actualSize)
        {
            return UpdatePendingFileAction.VerifyAgain;
        }

        return record.ModifiedUtc == actualModifiedUtc
            ? UpdatePendingFileAction.Reuse
            : UpdatePendingFileAction.VerifyAgain;
    }

    /// <summary>
    /// 为一次通过校验的下载创建记录。长度取实际收到的字节数，因为它描述的是本机文件。
    /// Creates the record for a download that passed verification. The length is the number of bytes actually
    /// received, because it describes the local file.
    /// </summary>
    /// <param name="path">安装包位置。/ Installer path.</param>
    /// <param name="version">该安装包的版本。/ Version of the installer.</param>
    /// <param name="asset">清单里的安装包。/ Package from the manifest.</param>
    /// <param name="receivedBytes">本次下载实际收到的字节数。/ Bytes actually received by this download.</param>
    /// <param name="modifiedUtc">校验完成时的文件写入时间（UTC）。/ File write time in UTC when verification finished.</param>
    public static UpdatePendingFileRecord CreateRecord(
        string path,
        string version,
        UpdatePackageAsset asset,
        long receivedBytes,
        DateTimeOffset modifiedUtc) =>
        new(path, version, asset.Sha256.ToLowerInvariant(), receivedBytes, modifiedUtc);
}
