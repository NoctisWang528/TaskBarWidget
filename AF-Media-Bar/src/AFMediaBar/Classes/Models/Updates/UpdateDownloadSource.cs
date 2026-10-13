namespace AFMediaBar.Classes.Models.Updates;

/// <summary>
/// 一个可尝试的下载来源：清单里的直链，或由它展开出的加速地址。
///
/// 它是数据而不是策略：主机名与"是否经过加速"要一路传到界面上，用户才能看出这次下载走的是直连还是加速站点。
/// One downloadable source to try: a direct link from the manifest, or an accelerator address expanded from it.
///
/// This is data rather than policy: the host name and the accelerated flag travel all the way to the interface so
/// the user can see whether the download went direct or through an accelerator.
/// </summary>
/// <param name="Url">完整下载地址。/ Complete download address.</param>
/// <param name="HostName">该地址的主机名（小写）。/ Host name in lower case.</param>
/// <param name="IsAccelerated">是否为加速地址。/ Whether this is an accelerated address.</param>
public sealed record UpdateDownloadSource(string Url, string HostName, bool IsAccelerated);
