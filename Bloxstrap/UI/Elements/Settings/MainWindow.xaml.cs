using Bloxstrap.UI.Elements.Settings.Pages;
using Bloxstrap.UI.ViewModels.Settings;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Common;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Bloxstrap.UI.Elements.Settings
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : INavigationWindow
    {
        private Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;
        private MainWindowViewModel viewModel = null!;
        public MainWindow(bool showAlreadyRunningWarning)
        {
            viewModel = new MainWindowViewModel();

            viewModel.RequestSaveNoticeEvent += (_, _) => SettingsSavedSnackbar.Show();
            viewModel.RequestCloseWindowEvent += (_, _) => Close();

            DataContext = viewModel;

            InitializeComponent();

            App.Logger.WriteLine("MainWindow", "Initializing settings window");

            if (showAlreadyRunningWarning)
                ShowAlreadyRunningSnackbar();

            // Competitive controls work before Roblox creates GlobalBasicSettings.
            // Only the nested GBS panel is gated by GBSEnabled.

            LoadState();

            string? lastPageName = App.State.Prop.LastPage;
            Type? lastPage = lastPageName is null ? null : Type.GetType(lastPageName);

            App.RemoteData.Subscribe((object? sender, EventArgs e) => {
                RemoteDataBase Data = App.RemoteData.Prop;

                AlertBar.Visibility = Data.AlertEnabled ? Visibility.Visible : Visibility.Collapsed;
                AlertBar.Message = Data.AlertContent;
                AlertBar.Severity = Data.AlertSeverity;
            });

            App.WindowsBackdrop();

            if (lastPage != null)
                SafeNavigate(lastPage);
            else
                RootNavigation.SelectedPageIndex = 0;

            RootNavigation.Navigated += OnNavigation!;

            void OnNavigation(object? sender, RoutedNavigationEventArgs e)
            {
                INavigationItem? currentPage = RootNavigation.Current;
                App.State.Prop.LastPage = currentPage?.PageType.FullName!;
            }
        }

        private async void SafeNavigate(Type page)
        {
            await Task.Delay(500); // ensure page service is ready

            if (page != typeof(CompetitivePage) && page != typeof(RegionSelectorPage) &&
                page != typeof(AdvancedRobloxSettingsPage) && page != typeof(DepthStrapSettingsPage) &&
                page != typeof(AppearancePage) && page != typeof(FastFlagEditorPage) && page != typeof(AntiApiPage))
                page = typeof(CompetitivePage);

            Navigate(page);
        }

        public void LoadState()
        {
            // The app uses WPF system DPI awareness, so all screen coordinates share the primary scale.
            double scale = UI.Utility.WindowScaling.ScaleFactor;
            var workAreas = new List<Rect> { SystemParameters.WorkArea };
            foreach (var screen in System.Windows.Forms.Screen.AllScreens.Where(screen => !screen.Primary))
            {
                var area = screen.WorkingArea;
                workAreas.Add(new Rect(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale));
            }
            var bounds = FitWindowBounds(_state, workAreas);
            MinWidth = Math.Min(960, bounds.Width);
            MinHeight = Math.Min(600, bounds.Height);
            Width = bounds.Width;
            Height = bounds.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
        }

        internal static Rect FitWindowBounds(Models.Persistable.WindowState state, IReadOnlyList<Rect> workAreas)
        {
            var workArea = workAreas[0];
            bool savedPosition = double.IsFinite(state.Left) && double.IsFinite(state.Top) &&
                double.IsFinite(state.Width) && state.Width > 0 && double.IsFinite(state.Height) && state.Height > 0;
            if (savedPosition)
            {
                var saved = new Rect(state.Left, state.Top, state.Width, state.Height);
                double bestOverlap = -1, bestDistance = double.PositiveInfinity;
                foreach (var candidate in workAreas)
                {
                    var intersection = Rect.Intersect(saved, candidate);
                    double overlap = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
                    double dx = state.Left - Math.Clamp(state.Left, candidate.Left, candidate.Right);
                    double dy = state.Top - Math.Clamp(state.Top, candidate.Top, candidate.Bottom);
                    double distance = dx * dx + dy * dy;
                    if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
                    {
                        workArea = candidate;
                        bestOverlap = overlap;
                        bestDistance = distance;
                    }
                }
            }
            double width = Math.Clamp(double.IsFinite(state.Width) && state.Width > 0 ? state.Width : 1280,
                Math.Min(960, workArea.Width), workArea.Width);
            double height = Math.Clamp(double.IsFinite(state.Height) && state.Height > 0 ? state.Height : 800,
                Math.Min(600, workArea.Height), workArea.Height);
            double left = savedPosition ? Math.Clamp(state.Left, workArea.Left, workArea.Right - width) : workArea.Left + (workArea.Width - width) / 2;
            double top = savedPosition ? Math.Clamp(state.Top, workArea.Top, workArea.Bottom - height) : workArea.Top + (workArea.Height - height) / 2;
            return new Rect(left, top, width, height);
        }

        private async void ShowAlreadyRunningSnackbar()
        {
            await Task.Delay(500); // wait for everything to finish loading
            AlreadyRunningSnackbar.Show();
        }

        #region INavigationWindow methods

        public Frame GetFrame() => RootFrame;

        public INavigation GetNavigation() => RootNavigation;

        public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

        public void SetPageService(IPageService pageService) => RootNavigation.PageService = pageService;

        public void ShowWindow() => Show();

        public void CloseWindow() => Close();

        #endregion INavigationWindow methods

        private void WpfUiWindow_Closing(object sender, CancelEventArgs e)
        {
            if (App.CompleteUninstallHandoff) return;
            if (!viewModel.ConfirmDiscardChanges(() => Frontend.ShowMessageBox(Strings.Menu_UnsavedChanges,
                MessageBoxImage.Warning, MessageBoxButton.YesNo) == MessageBoxResult.Yes))
                e.Cancel = true;

            _state.Width = this.Width;
            _state.Height = this.Height;

            _state.Top = this.Top;
            _state.Left = this.Left;

            App.State.Save();
        }

        private void WpfUiWindow_Closed(object sender, EventArgs e)
        {
            viewModel.Dispose();
            if (App.CompleteUninstallHandoff) return;
            if (App.LaunchSettings.TestModeFlag.Active)
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
            else
                App.SoftTerminate();
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.Settings.Prop.SaveAndLaunchToPlayer)
            {
                viewModel.SaveAndLaunch("player");
            }
            else
            {
                var menu = LaunchButton.ContextMenu;
                menu.PlacementTarget = LaunchButton;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.VerticalOffset = -4;
                menu.IsOpen = true;
            }
        }

        public void ShowLoading(string message = "Loading...")
        {
            LoadingOverlayText.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        public void HideLoading()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }
}
