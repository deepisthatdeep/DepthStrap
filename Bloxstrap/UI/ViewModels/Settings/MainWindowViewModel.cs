using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Input;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public class MainWindowViewModel : NotifyPropertyChangedViewModel
    {
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
            const string LOG_IDENT = "MainWindowViewModel::SaveSettings";

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

            foreach (var pair in App.PendingSettingTasks)
            {
                var task = pair.Value;

                if (task.Changed)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Executing pending task '{task}'");
                    task.Execute();
                }
            }

            App.PendingSettingTasks.Clear();

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
