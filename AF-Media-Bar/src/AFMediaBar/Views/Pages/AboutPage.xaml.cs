using System.Windows;
using System.Windows.Controls;
using AFMediaBar.Classes.Utils;
using AFMediaBar.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace AFMediaBar.Views.Pages
{
    /// <summary>
    /// 关于页：开发人员、赞助者名单、赞助我、开源许可与项目信息。
    ///
    /// 它只负责展示，本身不解析设置、不发起网络请求：名单由服务层填进视图模型，许可清单是一份可核对的静态目录，
    /// 而"哪些设置项在哪里"属于「应用」页。它由原「应用与关于」页的后半部分独立而来。
    /// About page: developers, the sponsor list, how to support, open-source licenses, and project information.
    ///
    /// It only presents. It parses no settings and issues no network requests itself: the name lists are filled into the view model by
    /// the service layer, the license catalog is a checkable static list, and "which settings live where" belongs to the application
    /// page. It was split out of the second half of the former "application and about" page.
    /// </summary>
    public partial class AboutPage : INavigableView<AboutViewModel>
    {
        public AboutViewModel ViewModel { get; }

        /// <summary>创建关于页。/ Creates the about page.</summary>
        /// <param name="viewModel">本页视图模型。/ This page's view model.</param>
        public AboutPage(AboutViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        /// <summary>页面首次加载时执行入场揭示。/ Reveals the page on first load.</summary>
        private void OnPageLoaded(object sender, RoutedEventArgs e) => SettingsRevealAnimator.Play(sender as Panel);
    }
}
