namespace AFMediaBar.Classes.Models.Credits;

/// <summary>
/// 一位贡献者。字段刻意只保留界面要显示的几项：GitHub 的贡献者响应还有几十个字段，而它们既用不上、
/// 又会让缓存文件变大。
/// One contributor. Only the fields the interface shows are kept: GitHub's contributors response carries dozens
/// more, none of which are used and all of which would bloat the cache file.
/// </summary>
/// <param name="Login">GitHub 用户名 / GitHub login.</param>
/// <param name="Contributions">提交次数（GitHub 的口径）/ Contribution count, in GitHub's own terms.</param>
/// <param name="ProfileUrl">主页地址 / Profile address.</param>
/// <param name="AvatarUrl">头像地址 / Avatar address.</param>
public sealed record ContributorInfo(string Login, int Contributions, string ProfileUrl, string? AvatarUrl);

/// <summary>
/// 一位赞助者。
/// One sponsor.
///
/// 名单来自仓库里的 <c>docs/sponsors.json</c>（与更新清单同一套读取方式），因此这里不猜任何字段：
/// 全部来自那份文件，缺省即不显示。
/// The list comes from <c>docs/sponsors.json</c> in the repository, read the same way as the update manifest, so nothing
/// here is guessed: every field comes from that file, and a missing one is simply not shown.
/// </summary>
/// <param name="Name">显示名称 / Display name.</param>
/// <param name="Tier">档位标识（例如 <c>gold</c>、<c>silver</c>、<c>backer</c>）/ Tier identifier such as <c>gold</c>, <c>silver</c>, or <c>backer</c>.</param>
/// <param name="Note">一句话备注 / A one-line note.</param>
/// <param name="Url">主页或留言链接 / Profile or message link.</param>
/// <param name="Since">支持起始时间（形如 <c>2026-01</c>）/ When the support started, formatted as <c>2026-01</c>.</param>
public sealed record SponsorInfo(string Name, string Tier, string? Note, string? Url, string? Since);

/// <summary>
/// 赞助名单文件（<c>docs/sponsors.json</c>）的内容。
/// The contents of the sponsor file, <c>docs/sponsors.json</c>.
/// </summary>
/// <param name="SchemaVersion">文件结构版本 / File schema version.</param>
/// <param name="Sponsors">按文件顺序排列的赞助者 / Sponsors in file order.</param>
public sealed record SponsorsDocument(int SchemaVersion, IReadOnlyList<SponsorInfo> Sponsors);

/// <summary>
/// 一份开源许可见条目。名称、版本、许可与主页地址都必须与项目文件里的真实依赖一致，
/// 否则这一节就成了装饰。
/// One open-source license entry. Name, version, license, and project address all have to match the real dependencies
/// in the project file, otherwise this section is decoration.
/// </summary>
/// <param name="Name">包名或项目名 / Package or project name.</param>
/// <param name="Version">版本（衍生代码留空）/ Version, left empty for derived code.</param>
/// <param name="License">许可标识（SPDX）/ License identifier (SPDX).</param>
/// <param name="Url">项目主页 / Project address.</param>
/// <param name="Note">补充说明（例如"仅部分文件衍生自该项目"）/ Additional note, such as "only some files derive from this project".</param>
public sealed record LicenseEntry(string Name, string Version, string License, string Url, string? Note = null);

/// <summary>
/// 关于页要展示的一份赞助收款码。
/// One payment QR code shown on the about page.
/// </summary>
/// <param name="TitleKey">标题文案键 / Title string key.</param>
/// <param name="AssetPath">收款码的包内路径。/ Pack path of the QR image.</param>
public sealed record SupportEntry(string TitleKey, string AssetPath);
