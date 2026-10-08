using System.Windows;
using Bloxstrap.UI.ViewModels.Installer;

namespace Bloxstrap.UI.Elements.Installer.Pages
{
    /// <summary>
    /// Interaction logic for CompletionPage.xaml
    /// </summary>
    public partial class CompletionPage
    {
        private readonly CompletionViewModel _viewModel = new();
        public CompletionPage()
        {
            _viewModel.CloseWindowRequest += (_, closeAction) =>
            {
                if (Window.GetWindow(this) is MainWindow window)
                {
                    window.CloseAction = closeAction;
                    window.Close();
                }
            };

            DataContext = _viewModel;
            InitializeComponent();
        }

        private void UiPage_Loaded(object sender, RoutedEventArgs e)
        {
            RestartNotice.Visibility = Networking.SystemPerformanceTuning.RestartRecommended ? Visibility.Visible : Visibility.Collapsed;
            if (Window.GetWindow(this) is MainWindow window)
            {
                window.SetNextButtonText(Strings.Common_Navigation_Next);
                window.SetButtonEnabled("back", false);
            }
        }
        private void RestartWindows_Click(object sender, RoutedEventArgs e)
        {
            if (Networking.SystemPerformanceTuning.ConfirmRestart()) Window.GetWindow(this)?.Close();
        }
    }
}
