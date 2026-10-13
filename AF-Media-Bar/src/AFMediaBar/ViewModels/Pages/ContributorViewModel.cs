using System.Windows.Media;
using AFMediaBar.Classes.Models.Credits;
using AFMediaBar.Resources;

namespace AFMediaBar.ViewModels.Pages
{
    /// <summary>
    /// 开发人员名单里的一位：头像 + 用户名 + 提交次数。
    ///
    /// 头像是**异步**填进来的：它要先下载再解码，而这条名单是在打开「关于」页时一次性建出来的。等图下完再显示整页是不可接受的，
    /// 因此先出名字与图标，图到了再换上去；取不到就停在图标上，不留空框。
    /// One entry in the developer list: avatar, login, and contribution count.
    ///
    /// The avatar is filled in **asynchronously**: it has to be downloaded and decoded, while this list is built in one go when the about page opens.
    /// Holding the whole page until the images arrive would be unacceptable, so the name and the fallback glyph appear first and the image replaces the
    /// glyph when it arrives; if it never arrives, the glyph stays and no empty box is left behind.
    /// </summary>
    public sealed partial class ContributorViewModel : ObservableObject
    {
        /// <summary>创建一位贡献者。/ Creates one contributor.</summary>
        /// <param name="contributor">名单里的原始条目。/ The raw entry from the list.</param>
        public ContributorViewModel(ContributorInfo contributor)
        {
            Login = contributor.Login;
            Contributions = contributor.Contributions;
            ProfileUrl = contributor.ProfileUrl;
            AvatarUrl = contributor.AvatarUrl;
        }

        /// <summary>GitHub 用户名。/ The GitHub login.</summary>
        public string Login { get; }

        /// <summary>提交次数。/ The contribution count.</summary>
        public int Contributions { get; }

        /// <summary>主页地址。/ The profile address.</summary>
        public string ProfileUrl { get; }

        /// <summary>头像地址（可能为空）。/ The avatar address, possibly null.</summary>
        public string? AvatarUrl { get; }

        /// <summary>头像；还没取到或取不到时为空。/ The avatar, null while it has not arrived or when it cannot be fetched.</summary>
        [ObservableProperty]
        private ImageSource? _avatar;

        /// <summary>是否已经有头像（界面据此把占位图标换掉）。/ Whether an avatar is present, which the interface uses to replace the fallback glyph.</summary>
        [ObservableProperty]
        private bool _hasAvatar;

        /// <summary>提交次数的展示文本（按当前语言拼出，因此语言变化后由页面重建整份名单）。/ The display text of the contribution count, composed in the active language, so a language change rebuilds the list on the page.</summary>
        public string ContributionsText => Translations.Format("Credits.Contributions", Contributions);

        /// <summary>
        /// 把一个头像填进来并标记已取到。
        /// Fills in one avatar and marks it as present.
        /// </summary>
        /// <param name="image">头像位图；为空表示取不到。/ The avatar bitmap, or null when it could not be fetched.</param>
        public void ApplyAvatar(ImageSource? image)
        {
            Avatar = image;
            HasAvatar = image is not null;
        }
    }
}
