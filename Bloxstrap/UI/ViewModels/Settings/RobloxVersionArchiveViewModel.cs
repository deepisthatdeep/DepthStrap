using System.Collections.ObjectModel;
using System.Windows.Input;
using Bloxstrap.Roblox;
using CommunityToolkit.Mvvm.Input;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public sealed class RobloxVersionArchiveViewModel : NotifyPropertyChangedViewModel
    {
        private readonly AsyncRelayCommand _downgrade, _install;
        private readonly RelayCommand _latest, _refresh, _cancel;
        private CancellationTokenSource? _lookupCancellation;
        private bool _busy;
        private string _version = App.Settings.Prop.RobloxPlayerVersionOverride;
        private string _status = "Latest Roblox release selected.";
        public Action? UpdatePolicyChanged { get; set; }
        internal Func<CancellationToken, Task<string>> PreviousLookup { get; set; } = WeaoDowngradeSource.GetPreviousAsync;
        internal Func<Task<int>> RunInstaller { get; set; } = RunInstallerAsync;
        public ObservableCollection<string> CachedVersions { get; } = new();
        private DistributionState? _installed;
        public string InstalledBuild => _installed is null ? "Unavailable" :
            _installed.InstallationPending ? "Installation incomplete; repair required" :
            RobloxVersionArchive.IsVersionId(_installed.VersionGuid) ? _installed.VersionGuid : "No installed build recorded";
        public bool CanEditVersion => !_busy;
        public string VersionId { get => _version; set { if (!_busy) SetVersion(value ?? ""); } }
        private void SetVersion(string value) { _version = value; OnPropertyChanged(nameof(VersionId)); OnPropertyChanged(nameof(SelectedCachedVersion)); }
        public string? SelectedCachedVersion { get => CachedVersions.FirstOrDefault(x => x == VersionId); set { if (value is not null) VersionId = value; } }
        public string Status { get => _status; private set { _status = value; OnPropertyChanged(nameof(Status)); } }
        public ICommand OpenDowngradeSourceCommand { get; }
        public ICommand DowngradePreviousCommand => _downgrade;
        public ICommand InstallVersionCommand => _install;
        public ICommand UseLatestCommand => _latest;
        public ICommand RefreshVersionsCommand => _refresh;
        public ICommand CancelLookupCommand => _cancel;

        public RobloxVersionArchiveViewModel()
        {
            _downgrade = new(DowngradePreviousAsync, () => !_busy);
            _install = new(InstallAsync, () => !_busy);
            _latest = new(UseLatest, () => !_busy);
            _refresh = new(RefreshVersions, () => !_busy);
            _cancel = new(CancelLookup, () => _lookupCancellation is not null);
            OpenDowngradeSourceCommand = new RelayCommand(() => Utilities.ShellExecute(WeaoDowngradeSource.DownloadLink(VersionId)));
            if (App.Settings.Prop.PauseRobloxUpdates) Status = "Roblox updates paused. The installed Player version is retained; the latest is installed when no build exists.";
            if (VersionId.Length > 0) Status = "Pinned to " + VersionId;
            TryRefreshVersions();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            OnPropertyChanged(nameof(CanEditVersion));
            _downgrade.NotifyCanExecuteChanged(); _install.NotifyCanExecuteChanged();
            _latest.NotifyCanExecuteChanged(); _refresh.NotifyCanExecuteChanged(); _cancel.NotifyCanExecuteChanged();
        }

        internal void CancelLookup() => _lookupCancellation?.Cancel();
        private bool CanInstall()
        {
            if (!CompetitiveSettingsBackup.PlayerPresence()) return true;
            Status = "Close all Roblox clients before installing another version.";
            return false;
        }

        private async Task DowngradePreviousAsync()
        {
            if (_busy || !CanInstall()) return;
            using var cancellation = new CancellationTokenSource();
            _lookupCancellation = cancellation;
            SetBusy(true);
            Status = "Getting the previous Windows Player build from WEAO RDD…";
            try
            {
                string version = await PreviousLookup(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _lookupCancellation = null;
                _cancel.NotifyCanExecuteChanged();
                SetVersion(version);
                await InstallCoreAsync(version);
            }
            catch (OperationCanceledException) { Status = "Downgrade lookup cancelled. Your saved version selection is unchanged."; }
            catch (Exception ex) { Status = "WEAO RDD downgrade failed: " + ex.Message + " Open the source site to select a version manually."; }
            finally { _lookupCancellation = null; SetBusy(false); }
        }

        private async Task InstallAsync()
        {
            if (_busy || !CanInstall()) return;
            SetBusy(true);
            try { await InstallCoreAsync(VersionId); }
            catch (Exception ex) { Status = "Could not start installation: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task InstallCoreAsync(string requested)
        {
            string version = requested.Trim().ToLowerInvariant();
            if (!RobloxVersionArchive.IsVersionId(version))
            { Status = "Enter version- followed by exactly 16 hexadecimal characters."; return; }
            if (!CanInstall()) return;
            SaveSelection(version, latest: false);
            UpdatePolicyChanged?.Invoke();
            Status = "Installing " + version + ". Downgrade source: rdd.weao.gg (WEAO RDD). Packages come from Roblox and are verified against its manifest.";
            int exitCode = await RunInstaller();
            bool refreshed = TryRefreshVersions();
            if (exitCode != 0)
                Status = "Installer exited with code " + exitCode + ". The saved version selection remains pinned; retry or select Use latest.";
            else if (refreshed && _installed is { InstallationPending: false } &&
                string.Equals(_installed.VersionGuid, version, StringComparison.OrdinalIgnoreCase) && CachedVersions.Contains(version))
                Status = "Installer finished for " + version + ". The selected build remains pinned. Launch Roblox when ready.";
            else
                Status = "The installer returned success, but the selected build could not be confirmed. Refresh builds or retry installation before launching.";
        }

        private void SaveSelection(string version, bool latest)
        {
            var settings = App.Settings.Prop;
            var previous = (settings.RobloxPlayerVersionOverride, settings.PauseRobloxUpdates, settings.UpdateRoblox, settings.StaticDirectory);
            settings.RobloxPlayerVersionOverride = version;
            settings.PauseRobloxUpdates = !latest;
            if (latest) settings.UpdateRoblox = true;
            else settings.StaticDirectory = false;
            try { Networking.AdaptiveRegionService.SaveUserSettings(); }
            catch
            {
                (settings.RobloxPlayerVersionOverride, settings.PauseRobloxUpdates, settings.UpdateRoblox, settings.StaticDirectory) = previous;
                throw;
            }
        }

        private void UseLatest()
        {
            if (_busy) return;
            try
            {
                SaveSelection("", latest: true);
                SetVersion("");
                Status = "Latest Roblox release selected. Launch Roblox to install it.";
                UpdatePolicyChanged?.Invoke();
            }
            catch (Exception ex) { Status = "Could not save the latest-version selection: " + ex.Message; }
        }

        private void RefreshVersions() => TryRefreshVersions();

        private bool TryRefreshVersions()
        {
            try
            {
                var versions = RobloxVersionArchive.InstalledPlayerVersions();
                if (!App.PlayerState.Load(false) && App.PlayerState.LastLoadFailed)
                    throw new IOException("The installed Player state could not be read.");
                _installed = App.PlayerState.IsSaved ? App.PlayerState.Prop : new DistributionState();
                CachedVersions.Clear();
                foreach (var version in versions) CachedVersions.Add(version);
                OnPropertyChanged(nameof(InstalledBuild));
                OnPropertyChanged(nameof(SelectedCachedVersion));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _installed = null;
                OnPropertyChanged(nameof(InstalledBuild));
                App.Logger.WriteException("RobloxVersionArchive::Refresh", ex);
                Status = "Could not refresh installed builds. Existing choices were kept; retry Refresh builds.";
                return false;
            }
        }

        internal static ProcessStartInfo InstallerStartInfo() => new(Paths.Application, "-player -force -nolaunch") { UseShellExecute = false };

        private static async Task<int> RunInstallerAsync()
        {
            using var installer = Process.Start(InstallerStartInfo());
            if (installer is null) throw new IOException("The Roblox installer could not be started.");
            await installer.WaitForExitAsync();
            return installer.ExitCode;
        }
    }
}
