using Bloxstrap.UI.Elements.Dialogs;
using Bloxstrap.UI.ViewModels.Settings;
using System.Windows;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    /// <summary>
    /// Interaction logic for AdvancedRobloxSettingsPage.xaml
    /// Roblox launch behavior settings (split out of the old Bootstrapper/Behaviour page).
    /// </summary>
    public partial class AdvancedRobloxSettingsPage
    {
        public AdvancedRobloxSettingsPage()
        {
            DataContext = new BehaviourViewModel();
            InitializeComponent();
            VersionSettingsPanel.DataContext = new RobloxVersionArchiveViewModel { UpdatePolicyChanged = ((BehaviourViewModel)DataContext).RefreshUpdatePolicy };
            Loaded += async (_, _) => await ((RobloxVersionArchiveViewModel)VersionSettingsPanel.DataContext).LoadReleaseLabelsAsync();
            Unloaded += (_, _) => ((RobloxVersionArchiveViewModel)VersionSettingsPanel.DataContext).CancelLookup();

            App.FrostRPC?.SetPage("Advanced Roblox Settings");
        }

        private void OpenMultiblox_Click(object sender, RoutedEventArgs e)
        {
            var window = new MultibloxDialog
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }
    }
}
