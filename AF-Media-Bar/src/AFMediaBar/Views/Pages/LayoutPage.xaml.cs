using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using AFMediaBar.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace AFMediaBar.Views.Pages
{
    /// <summary>
    /// LayoutPage.xaml 的交互逻辑
    /// </summary>
    public partial class LayoutPage : INavigableView<LayoutViewModel>
    {
        public LayoutViewModel ViewModel { get; }

        public LayoutPage(LayoutViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            // 传的是作用域名称的文案键，句子由语言文件拼装：切换语言后对话框跟着换语言。
            // The argument is the localization key of the scope name; the language file composes the sentence, so the dialog
            // follows a language change.
            if (await SettingsResetDialog.ConfirmAsync("Layout.Page.Title")) ViewModel.ResetLayout();
        }
    }
}
