using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Input;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public class MainWindowViewModel : NotifyPropertyChangedViewModel, IDisposable
    {
        private string _savedEditableSettings;

        public MainWindowViewModel()
        {
            _savedEditableSettings = EditableSettingsSnapshot(App.Settings.Prop);
            App.Settings.Saved += SettingsPersisted;
        }

        private void SettingsPersisted(string json) =>
            _savedEditableSettings = EditableSettingsSnapshot(JsonSerializer.Deserialize<Models.Persistable.Settings>(json)!);

        public void Dispose() => App.Settings.Saved -= SettingsPersisted;

        internal bool HasUnsavedSettingsChanges => _savedEditableSettings != EditableSettingsSnapshot(App.Settings.Prop);

        internal bool ConfirmDiscardChanges(Func<bool> confirmDiscard) =>
            !(HasUnsavedSettingsChanges || App.FastFlags.Changed || App.PendingSettingTasks.Any()) || confirmDiscard();

        internal static string EditableSettingsSnapshot(Models.Persistable.Settings settings)
        {
            var snapshot = JsonSerializer.SerializeToNode(settings)!.AsObject();
            foreach (string name in new[] { nameof(settings.AppearanceVersion), nameof(settings.NetworkSetupVersion),
                nameof(settings.SystemPerformanceOfferShown), nameof(settings.RegionCalibrationCompleted),
                nameof(settings.RegionMonitoringVersion), nameof(settings.IsNavigationSidebarExpanded) })
                snapshot.Remove(name);
            if (settings.AdaptiveRegionPreferencesEnabled)
            {
                snapshot.Remove(nameof(settings.CompetitivePreferredCity));
                snapshot.Remove(nameof(settings.CompetitiveFallbackCities));
            }
            if (settings.AutomaticRegionalPreference)
            {
                snapshot.Remove(nameof(settings.PreferNorthAmericaOnly));
                snapshot.Remove(nameof(settings.PreferEuropeOnly));
            }
            if (settings.MatchFpsToMonitorRefreshRate) snapshot.Remove(nameof(settings.CompetitiveFpsCap));
            return snapshot.ToJsonString();
        }

        public ICommand OpenAboutCommand => new RelayCommand(OpenAbout);
        public ICommand SaveSettingsCommand => new RelayCommand(SaveSettings);
        public ICommand SaveAndLaunchPlayerCommand => new RelayCommand(() => SaveAndLaunch("player"));
        public ICommand SaveAndLaunchStudioCommand => new RelayCommand(() => SaveAndLaunch("studio"));
        public ICommand RestartAppCommand => new RelayCommand(RestartApp);
        public ICommand CloseWindowCommand => new RelayCommand(CloseWindow);

        public EventHandler? RequestSaveNoticeEvent;
        public EventHandler? RequestCloseWindowEvent;
        public bool GBSEnabled = App.GlobalSettings.Loaded;
        public event EventHandler? SettingsSaved;

        public bool TestModeEnabled
        {
            get => App.LaunchSettings.TestModeFlag.Active;
            set
            {
                if (value && !App.State.Prop.TestModeWarningShown)
                {
                    var result = Frontend.ShowMessageBox(Strings.Menu_TestMode_Prompt, MessageBoxImage.Information, MessageBoxButton.YesNo);
                    if (result != MessageBoxResult.Yes)
                        return;

                    App.State.Prop.TestModeWarningShown = true;
                }

                App.LaunchSettings.TestModeFlag.Active = value;
            }
        }

        public bool IsSidebarExpanded
        {
            get => App.Settings.Prop.IsNavigationSidebarExpanded;
            set => App.Settings.Prop.IsNavigationSidebarExpanded = value;
        }

        private void OpenAbout()
        {
            App.FrostRPC?.SetDialog("About");

            new Elements.About.MainWindow().ShowDialog();

            App.FrostRPC?.ClearDialog();
        }

        private void CloseWindow() => RequestCloseWindowEvent?.Invoke(this, EventArgs.Empty);

        public void SaveSettings()
        {
            TrySaveSettings();
        }

        private bool TrySaveSettings()
        {
            try { Networking.AdaptiveRegionService.SaveUserSettings(); }
            catch (IOException ex)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning);
                return false;
            }
            App.State.Save();
            if (!App.State.LastSaveSucceeded) return false;
            App.FastFlags.Save();
            if (!App.FastFlags.LastSaveSucceeded) return false;
            App.GlobalSettings.Save();
            if (!App.GlobalSettings.LastSaveSucceeded)
            {
                Frontend.ShowMessageBox("Roblox settings could not be saved. Your existing file was preserved; close Roblox and retry.", MessageBoxImage.Warning);
                return false;
            }

            if (!PendingSettingsExecutor.TryExecute(out string? failedTask))
            {
                Frontend.ShowMessageBox($"Could not finish applying setting '{failedTask}'. Completed changes were kept. Remaining changes stay queued; correct any reported problem and retry Save.", MessageBoxImage.Warning);
                return false;
            }

            RequestSaveNoticeEvent?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void SaveAndLaunch(string mode)
        {
            if (!TrySaveSettings()) return;

            if (!App.LaunchSettings.TestModeFlag.Active)
                Process.Start(Paths.Application, $"-{mode.ToLower()}");
            else
                CloseWindow();
        }

        private async void RestartApp()
        {
            if (!TrySaveSettings()) return;

            SettingsSaved?.Invoke(this, EventArgs.Empty);

            await Task.Delay(750);

            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = "-menu"
            };

            Process.Start(startInfo);

            App.FrostRPC?.Dispose();
            CloseWindow();
        }
    }
}
