using Bloxstrap.UI.Elements.ContextMenu;
using Bloxstrap.UI.Elements.Dialogs;
using Bloxstrap.UI.ViewModels.Settings;
using System.Windows;
using System.Windows.Controls;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for DepthStrapSettingsPage.xaml
    /// App-level settings: channel/updates/static directory, cleaner and bootstrapper dialog customization.
    /// </summary>
    public partial class DepthStrapSettingsPage
    {
        public DepthStrapSettingsPage()
        {
            DataContext = new DepthStrapSettingsViewModel(this);
            InitializeComponent();

            App.FrostRPC?.SetPage("DepthStrap Settings");
        }

        private void OpenChannelListDialog_Click(object sender, RoutedEventArgs e)
        {
            App.FrostRPC?.SetDialog("Channel List");

            var dialog = new ChannelListsDialog();
            dialog.Owner = Window.GetWindow(this);

            dialog.ShowDialog();

            App.FrostRPC?.ClearDialog();
        }
        private void SystemPerformance_Click(object sender, RoutedEventArgs e) =>
            new SystemPerformanceDialog { Owner = Window.GetWindow(this) }.ShowDialog();

        // custom launcher theme selection (moved from AppearancePage)
        public void CustomThemeSelection(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not DepthStrapSettingsViewModel vm) return;

            var viewModel = vm.Dialog;

            viewModel.SelectedCustomTheme = (string)((ListBox)sender).SelectedItem;
            viewModel.SelectedCustomThemeName = viewModel.SelectedCustomTheme;

            viewModel.OnPropertyChanged(nameof(viewModel.SelectedCustomTheme));
            viewModel.OnPropertyChanged(nameof(viewModel.SelectedCustomThemeName));
        }
    }
}
