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
        private int _suggestionRevision;
        private bool _suggestionsOpen;
        private string _suggestionStatus = "Type 741 or 742 to find matching Player releases.";
        internal Func<Task<string[]>> ReleaseCatalog { get; set; } = RobloxReleaseLookup.GetReleaseCatalogAsync;
        public ObservableCollection<string> ReleaseSuggestions { get; } = new();
        public string? SelectedReleaseSuggestion
        {
            get => null;
            set { if (value is not null && !_busy) { VersionId = value; SuggestionsOpen = false; } }
        }
        public bool SuggestionsOpen { get => _suggestionsOpen; set { _suggestionsOpen = value; OnPropertyChanged(nameof(SuggestionsOpen)); } }
        public string SuggestionStatus { get => _suggestionStatus; private set { _suggestionStatus = value; OnPropertyChanged(nameof(SuggestionStatus)); } }
        private string _version = App.Settings.Prop.RobloxPlayerVersionOverride;
        private string _status = "Latest Roblox release selected.";
        private int _releaseLabelRevision;
        private string _releaseSummary = "Current and previous Player release numbers load when this page opens.";
        private string _downgradeLabel = "Downgrade from WEAO RDD";
        public string ReleaseSummary { get => _releaseSummary; private set { _releaseSummary = value; OnPropertyChanged(nameof(ReleaseSummary)); } }
        public string DowngradeLabel { get => _downgradeLabel; private set { _downgradeLabel = value; OnPropertyChanged(nameof(DowngradeLabel)); } }
        internal Func<Task<(string? Current, string? Previous)>> ReleaseLabels { get; set; } = RobloxReleaseLookup.GetReleaseLabelsAsync;
        internal Func<string, string?> InstalledReleaseReader { get; set; } = ReadInstalledRelease;
        public Action? UpdatePolicyChanged { get; set; }
        internal Func<CancellationToken, Task<string>> PreviousLookup { get; set; } = WeaoDowngradeSource.GetPreviousAsync;
        internal Func<string, CancellationToken, Task<string>> ReleaseLookup { get; set; } = RobloxReleaseLookup.ResolveAsync;
        internal Func<Task<int>> RunInstaller { get; set; } = RunInstallerAsync;
        public ObservableCollection<string> CachedVersions { get; } = new();
        private DistributionState? _installed;
        public string InstalledBuild => _installed is null ? "Unavailable" :
            _installed.InstallationPending ? "Installation incomplete; repair required" :
            RobloxVersionArchive.IsVersionId(_installed.VersionGuid) ? DescribeInstalledBuild(_installed.VersionGuid) : "No installed build recorded";

        private string DescribeInstalledBuild(string hash)
        {
            string? release = InstalledReleaseReader(hash);
            return RobloxReleaseLookup.IsReleaseNumber(release) ? $"{ReleaseLabel(release!)}\n{hash}" : hash;
        }

        private static string? ReadInstalledRelease(string hash)
        {
            try
            {
                return RobloxClientVersion.ReadRelease(InstalledPlayerExecutable(hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }

        internal static string InstalledPlayerExecutable(string hash) => Path.Combine(Paths.Versions,
            App.Settings.Prop.StaticDirectory ? "WindowsPlayer" : hash, App.RobloxPlayerAppName);

        internal static string ReleaseLabel(string release) => $"{release.Split('.')[1]} ({release})";

        internal async Task LoadReleaseLabelsAsync()
        {
            int revision = ++_releaseLabelRevision;
            ReleaseSummary = "Checking current and previous Windows Player releases…";
            (string? Current, string? Previous) releases;
            try { releases = await ReleaseLabels(); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
            { releases = (null, null); }
            await ApplySuggestionsAsync(() =>
            {
                if (revision != _releaseLabelRevision) return;
                string current = RobloxReleaseLookup.IsReleaseNumber(releases.Current) ? ReleaseLabel(releases.Current!) : "unavailable";
                string previous = RobloxReleaseLookup.IsReleaseNumber(releases.Previous) ? ReleaseLabel(releases.Previous!) : "unavailable";
                ReleaseSummary = $"Current Roblox: {current}. Previous / downgrade: {previous}.";
                DowngradeLabel = RobloxReleaseLookup.IsReleaseNumber(releases.Previous) ? "Downgrade to " + ReleaseLabel(releases.Previous!) : "Downgrade from WEAO RDD";
            });
        }
        public bool CanEditVersion => !_busy;
        public string VersionId { get => _version; set { if (!_busy) SetVersion(value ?? ""); } }
        private void SetVersion(string value)
        {
            if (_version == value) return;
            _version = value; OnPropertyChanged(nameof(VersionId)); OnPropertyChanged(nameof(SelectedCachedVersion));
            _ = UpdateSuggestionsAsync();
        }

        internal async Task UpdateSuggestionsAsync()
        {
            int revision = ++_suggestionRevision;
            string query = VersionId.Trim();
            SuggestionsOpen = false;
            if (_busy || !RobloxReleaseLookup.IsSuggestionQuery(query))
            {
                ReleaseSuggestions.Clear();
                SuggestionStatus = "Type 741 or 742 to find matching Player releases.";
                return;
            }
            ReleaseSuggestions.Clear();
            SuggestionStatus = "Loading matching Player releases…";
            try
            {
                await Task.Delay(250);
                if (revision != _suggestionRevision || _busy) return;
                var releases = await ReleaseCatalog();
                var matches = RobloxReleaseLookup.MatchReleases(releases, query);
                await ApplySuggestionsAsync(() =>
                {
                    if (revision != _suggestionRevision || _busy || VersionId.Trim() != query) return;
                    foreach (string release in matches) ReleaseSuggestions.Add(release);
                    SuggestionsOpen = ReleaseSuggestions.Count > 0;
                    SuggestionStatus = SuggestionsOpen ? "Select a release, then click Install / roll back." : "No matching Windows Player releases found. You can still paste a complete release or build hash.";
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or RegexMatchTimeoutException or FormatException or OverflowException)
            {
                await ApplySuggestionsAsync(() =>
                {
                    if (revision == _suggestionRevision && !_busy)
                        SuggestionStatus = "Could not load release suggestions. Retry typing, or paste a complete release or build hash.";
                });
            }
        }

        private static Task ApplySuggestionsAsync(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess()) return dispatcher.InvokeAsync(action).Task;
            action();
            return Task.CompletedTask;
        }
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
            if (busy) { ++_suggestionRevision; SuggestionsOpen = false; }
            OnPropertyChanged(nameof(CanEditVersion));
            _downgrade.NotifyCanExecuteChanged(); _install.NotifyCanExecuteChanged();
            _latest.NotifyCanExecuteChanged(); _refresh.NotifyCanExecuteChanged(); _cancel.NotifyCanExecuteChanged();
        }

        internal void CancelLookup()
        {
            _lookupCancellation?.Cancel();
            ++_suggestionRevision;
            ++_releaseLabelRevision;
            SuggestionsOpen = false;
        }
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
            try
            {
                string requested = VersionId.Trim();
                if (RobloxReleaseLookup.IsReleaseNumber(requested))
                {
                    using var cancellation = new CancellationTokenSource();
                    _lookupCancellation = cancellation;
                    _cancel.NotifyCanExecuteChanged();
                    Status = "Looking up Windows Player release " + requested + "…";
                    string resolved = await ReleaseLookup(requested, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!RobloxVersionArchive.IsVersionId(resolved)) throw new InvalidDataException("Release lookup returned an invalid build hash.");
                    requested = resolved;
                    _lookupCancellation = null;
                    _cancel.NotifyCanExecuteChanged();
                    SetVersion(resolved);
                }
                await InstallCoreAsync(requested);
            }
            catch (OperationCanceledException) { Status = "Release lookup cancelled. Your saved version selection is unchanged."; }
            catch (Exception ex) { Status = "Could not start installation: " + ex.Message; }
            finally { _lookupCancellation = null; SetBusy(false); }
        }

        private async Task InstallCoreAsync(string requested)
        {
            string version = requested.Trim().ToLowerInvariant();
            if (!RobloxVersionArchive.IsVersionId(version))
            {
                Status = "Enter a version- build hash with 16 hexadecimal characters, or a full four-part release number such as 0.741.0.7411058.";
                return;
            }
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
