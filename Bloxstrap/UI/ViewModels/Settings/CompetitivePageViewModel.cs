using Bloxstrap.Competitive;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Bloxstrap.Integrations;
using CommunityToolkit.Mvvm.Input;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public class CompetitivePageViewModel : NotifyPropertyChangedViewModel
    {
        private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            OnPropertyChanged(name!);

        public CompetitivePageViewModel()
        {
            Roblox.MonitorRefreshRateService.ApplyDetectedCap();
            LoadHistory();

            // live session block: the watcher process writes a small state file; poll at 1 Hz.
            // (one-second UI refresh is plenty - do not update every field at 1 ms)
            _sessionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += (_, _) => PollSession();
        }

        /// <summary>
        /// Merged Roblox global settings (GBS) view model. The old standalone
        /// "Roblox Settings" page was folded into this page; its XAML binds to
        /// this instance through a nested DataContext.
        /// </summary>
        public RobloxSettingsViewModel Gbs { get; } = new();

        /// <summary>GBS section is only usable once Roblox has created its GlobalBasicSettings file.</summary>
        public bool GBSEnabled => App.GlobalSettings.Loaded;
        public bool ManualPerformanceControls => !App.Settings.Prop.CompetitiveAggressiveRendering;
        public bool ManualFpsEnabled => !MatchMonitorRefresh;
        public bool MatchMonitorRefresh
        {
            get => App.Settings.Prop.MatchFpsToMonitorRefreshRate;
            set { App.Settings.Prop.MatchFpsToMonitorRefreshRate = value; Roblox.MonitorRefreshRateService.ApplyDetectedCap(); NotifyAllChanged(); }
        }
        public bool AdaptiveRegions
        {
            get => App.Settings.Prop.AdaptiveRegionPreferencesEnabled;
            set { App.Settings.Prop.AdaptiveRegionPreferencesEnabled = value; Raise(); Raise(nameof(ManualRegionControls)); }
        }
        public bool ManualRegionControls => !AdaptiveRegions;
        public string CalibrationStatus => Networking.NetworkTestResult.Read()?.Status ?? Networking.AdaptiveRegionService.Status;
        public bool IcmpAvailable => Networking.NetworkTestResult.Read()?.IcmpAvailable ?? false;
        public bool CloudflareAvailable => Networking.NetworkTestResult.Read()?.CloudflareAvailable ?? false;
        public bool RegionsAvailable => Networking.NetworkTestResult.Read()?.RegionsAvailable ?? false;
        public bool TraceAvailable => Networking.NetworkTestResult.Read()?.TraceAvailable ?? false;
        public string FpsStatus => MatchMonitorRefresh ? Roblox.MonitorRefreshRateService.LastDetectedHz is int hz ? $"Automatic: {hz} FPS (monitor maximum Hz)" : $"Monitor detection unavailable; retained cap: {App.Settings.Prop.CompetitiveFpsCap} FPS" : App.Settings.Prop.CompetitiveFpsCap <= 0 ? "Manual: Uncapped / Roblox maximum" : $"Manual: {App.Settings.Prop.CompetitiveFpsCap} FPS";
        public ICommand RecalibrateRegionsCommand => new RelayCommand(() => ShowNetworkSetup(false));
        public ICommand ResetNetworkCommand => new RelayCommand(() => ShowNetworkSetup(true));
        private void ShowNetworkSetup(bool reset)
        {
            new UI.Elements.Dialogs.RegionCalibrationDialog(reset) { Owner = Application.Current.MainWindow }.ShowDialog();
            RecentMatches.Clear(); RunSummaries.Clear();
            LoadHistory();
            NotifyAllChanged();
        }
        private bool _warpChanging;
        private DateTime _nextWarpQuery = DateTime.MinValue;
        public async Task RefreshWarpStateAsync()
        {
            if (_warpChanging || DateTime.UtcNow < _nextWarpQuery) return;
            _nextWarpQuery = DateTime.UtcNow.AddSeconds(30);
            try { await CloudflareNetworkState.QueryAsync(CancellationToken.None, true); }
            catch (Exception ex) { App.Logger.WriteException("WarpState", ex); }
            OnPropertyChanged(nameof(WarpConnected)); OnPropertyChanged(nameof(WarpStatus));
        }
        public bool WarpCanControl => !_warpChanging && File.Exists(Networking.WarpClient.CliPath) && App.Settings.Prop.CloudflareTermsAccepted;
        public bool WarpConnected
        {
            get => CloudflareNetworkState.Cached?.WarpActive ?? false;
            set { if (!_warpChanging) _ = ChangeWarpAsync(value); }
        }
        public string WarpStatus => CloudflareNetworkState.Cached?.WarpActive switch
        {
            true => "Connected", false => "Disconnected", _ => "State unknown — run network setup"
        };
        private async Task ChangeWarpAsync(bool connected)
        {
            _warpChanging = true;
            OnPropertyChanged(nameof(WarpCanControl));
            using var gate = Networking.RegionCalibrationService.OpenOperationGate();
            bool acquired = gate.WaitOne(0);
            try
            {
                if (!acquired) throw new InvalidOperationException("A network test or WARP change is already running.");
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                await Networking.RouteComparisonRunner.SwitchAsync(new Networking.WarpClient(), connected,
                    ct => CloudflareNetworkState.QueryAsync(ct, true), budget.Token);
                await Networking.AdaptiveRegionService.RefreshAsync(budget.Token);
            }
            catch (Exception ex) { Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Warning); }
            finally
            {
                if (acquired) gate.Release();
                _warpChanging = false;
                OnPropertyChanged(nameof(WarpCanControl));
                OnPropertyChanged(nameof(WarpConnected)); OnPropertyChanged(nameof(WarpStatus)); OnPropertyChanged(nameof(CalibrationStatus));
            }
        }

        #region Master / Region
        public bool Enabled
        {
            get => App.Settings.Prop.CompetitiveModeEnabled;
            set
            {
                App.Settings.Prop.CompetitiveModeEnabled = value;
                Raise();
            }
        }

        public string PreferredCity
        {
            get => App.Settings.Prop.CompetitivePreferredCity;
            set
            {
                App.Settings.Prop.CompetitivePreferredCity = (value ?? "").Trim();
                Raise();
            }
        }

        /// <summary>One city per line, in fallback priority order.</summary>
        public string FallbackCitiesText
        {
            get => string.Join("\n", App.Settings.Prop.CompetitiveFallbackCities);
            set
            {
                var cities = (value ?? "")
                    .Split(new[] { '\n', '\r', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                App.Settings.Prop.CompetitiveFallbackCities = cities;
                Raise();
            }
        }

        public bool PreferNorthAmericaOnly
        {
            get => App.Settings.Prop.PreferNorthAmericaOnly;
            set { SetRegionalPreference(value, false); }
        }

        public bool PreferEuropeOnly
        {
            get => App.Settings.Prop.PreferEuropeOnly;
            set { SetRegionalPreference(false, value); }
        }
        private void SetRegionalPreference(bool northAmerica, bool europe)
        {
            App.Settings.Prop.PreferNorthAmericaOnly = northAmerica;
            App.Settings.Prop.PreferEuropeOnly = europe;
            App.Settings.Prop.AutomaticRegionalPreference = false;
            Raise(nameof(PreferNorthAmericaOnly)); Raise(nameof(PreferEuropeOnly));
            _ = RefreshRegionalPreferenceAsync();
        }
        private async Task RefreshRegionalPreferenceAsync()
        {
            try { await Networking.AdaptiveRegionService.RefreshAsync(CancellationToken.None); }
            catch (Exception ex) { App.Logger.WriteException("RegionalPreference", ex); }
            Raise(nameof(PreferredCity)); Raise(nameof(FallbackCitiesText));
        }
        public int MaxScanPages
        {
            get => App.Settings.Prop.PreferredRegionMaxPages;
            set { App.Settings.Prop.PreferredRegionMaxPages = Math.Clamp(value, 1, 50); Raise(); }
        }

        public int ScanTimeoutSeconds
        {
            get => App.Settings.Prop.PreferredRegionSearchTimeoutSeconds;
            set { App.Settings.Prop.PreferredRegionSearchTimeoutSeconds = Math.Clamp(value, 3, 120); Raise(); }
        }

        public bool AutoSelectPreferredServerOnLaunch
        {
            get => App.Settings.Prop.AutoSelectPreferredServerOnLaunch;
            set { App.Settings.Prop.AutoSelectPreferredServerOnLaunch = value; Raise(); }
        }

        #endregion

        #region Chime Monitor

        public bool ChimeMonitorEnabled
        {
            get => App.Settings.Prop.ChimeRegionMonitorEnabled;
            set { App.Settings.Prop.ChimeRegionMonitorEnabled = value; Raise(); }
        }

        public bool WarnOnBadChimeRegion
        {
            get => App.Settings.Prop.WarnOnBadChimeRegion;
            set { App.Settings.Prop.WarnOnBadChimeRegion = value; Raise(); }
        }

        public bool LogCompetitiveSessions
        {
            get => App.Settings.Prop.LogCompetitiveSessions;
            set { App.Settings.Prop.LogCompetitiveSessions = value; Raise(); }
        }

        public bool AutoLeaveBadChimeRegion
        {
            get => App.Settings.Prop.AutoLeaveBadChimeRegion;
            set
            {
                App.Settings.Prop.AutoLeaveBadChimeRegion = value;
                if (value) App.Settings.Prop.CompetitiveNetworkMonitorEnabled = true;
                Raise(); Raise(nameof(NetworkMonitorEnabled));
            }
        }
        #endregion

        #region Performance

        public ObservableCollection<ProcessPriorityOption> PriorityOptions { get; } =
            new(Enum.GetValues(typeof(ProcessPriorityOption)).Cast<ProcessPriorityOption>());

        public ProcessPriorityOption SelectedCompetitivePriority
        {
            get => CompetitivePerformanceManager.GetEffectivePriority();
            set
            {
                App.Settings.Prop.CompetitiveProcessPriority = value;
                App.Settings.Prop.SelectedProcessPriority = value;
                Raise();
                OnPropertyChanged(nameof(ShowRealtimeWarning));
            }
        }

        public bool ShowRealtimeWarning => SelectedCompetitivePriority == ProcessPriorityOption.RealTime;

        public bool DisablePowerThrottling
        {
            get => App.Settings.Prop.DisableRobloxPowerThrottling;
            set { App.Settings.Prop.DisableRobloxPowerThrottling = value; Raise(); }
        }

        public bool ReapplyPerformanceSettings
        {
            get => App.Settings.Prop.ReapplyPerformanceSettings;
            set { App.Settings.Prop.ReapplyPerformanceSettings = value; Raise(); }
        }

        public int ReapplyDelayMs
        {
            get => App.Settings.Prop.PerformanceReapplyDelayMs;
            set { App.Settings.Prop.PerformanceReapplyDelayMs = Math.Clamp(value, 500, 30000); Raise(); }
        }

        // --- FPS cap (real Roblox GlobalBasicSettings value, not the obsolete FF) ---
        public sealed class FpsCapOption
        {
            public int Value { get; init; }   // 0 = uncapped
            public string Label { get; init; } = "";
            public override string ToString() => Label;
        }

        public ObservableCollection<FpsCapOption> FpsOptions { get; } = new()
        {
            new() { Value = 120,  Label = "120" },
            new() { Value = 144,  Label = "144" },
            new() { Value = 165,  Label = "165" },
            new() { Value = 180,  Label = "180" },
            new() { Value = 240,  Label = "240 (recommended)" },
            new() { Value = 360,  Label = "360" },
            new() { Value = 0, Label = "Uncapped / Roblox maximum" },
            new() { Value = -1, Label = "Custom" }
        };

        private FpsCapOption CustomFpsOption => FpsOptions.Last();
        public FpsCapOption SelectedFpsPreset
        {
            get => FpsOptions.FirstOrDefault(o => o.Value == App.Settings.Prop.CompetitiveFpsCap) ?? CustomFpsOption;
            set
            {
                if (value is null || value.Value < 0) return;
                App.Settings.Prop.CompetitiveFpsCap = value.Value;
                OnPropertyChanged(nameof(SelectedFpsPreset));
                OnPropertyChanged(nameof(CustomFpsCapText));
            }
        }

        /// <summary>Direct custom cap entry (0-1000, 0 = uncapped).</summary>
        public string CustomFpsCapText
        {
            get => App.Settings.Prop.CompetitiveFpsCap.ToString();
            set
            {
                if (!int.TryParse((value ?? ""), out int cap) || cap < 0 || cap > 1000)
                    return; // keep old value until input is valid

                App.Settings.Prop.CompetitiveFpsCap = cap;
                OnPropertyChanged(nameof(CustomFpsCapText));
                OnPropertyChanged(nameof(SelectedFpsPreset));
            }
        }

        public bool LowGraphicsPreset
        {
            get => App.Settings.Prop.CompetitiveLowGraphicsPreset;
            set { App.Settings.Prop.CompetitiveLowGraphicsPreset = value; Raise(); }
        }

        public int GraphicsQualityLevel
        {
            get => App.Settings.Prop.CompetitiveGraphicsQualityLevel;
            set { App.Settings.Prop.CompetitiveGraphicsQualityLevel = Math.Clamp(value, 0, 21); Raise(); }
        }

        #endregion

        #region Rendering layer (FastFlag - best effort)

        public bool MSAA1x
        {
            get => App.Settings.Prop.CompetitiveMSAA1x;
            set { App.Settings.Prop.CompetitiveMSAA1x = value; Raise(); }
        }

        public bool DisableGrass
        {
            get => App.Settings.Prop.CompetitiveDisableGrass;
            set { App.Settings.Prop.CompetitiveDisableGrass = value; Raise(); }
        }

        public bool LowPolyMeshes
        {
            get => App.Settings.Prop.CompetitiveLowPolyMeshes;
            set { App.Settings.Prop.CompetitiveLowPolyMeshes = value; Raise(); }
        }

        public ObservableCollection<CompetitiveRendererOption> RendererOptions { get; } =
            new(Enum.GetValues(typeof(CompetitiveRendererOption)).Cast<CompetitiveRendererOption>());

        public CompetitiveRendererOption SelectedRenderer
        {
            get => App.Settings.Prop.CompetitiveRenderer;
            set { App.Settings.Prop.CompetitiveRenderer = value; Raise(); }
        }

        #endregion

        #region Advanced (CPU affinity)

        public enum AffinityMode { Automatic, CustomMask }

        public ObservableCollection<AffinityMode> AffinityModes { get; } = new() { AffinityMode.Automatic, AffinityMode.CustomMask };

        public AffinityMode SelectedAffinityMode
        {
            get => App.Settings.Prop.CustomCpuAffinityEnabled && App.Settings.Prop.CustomCpuAffinityMask != 0 ? AffinityMode.CustomMask : AffinityMode.Automatic;
            set
            {
                if (value == AffinityMode.CustomMask)
                    App.Settings.Prop.CustomCpuAffinityEnabled = true;
                else
                {
                    App.Settings.Prop.CustomCpuAffinityEnabled = false;
                    App.Settings.Prop.CustomCpuAffinityMask = 0;
                }

                Raise();
                OnPropertyChanged(nameof(CpuAffinityMaskText));
            }
        }

        public string CpuAffinityMaskText
        {
            get => $"0x{App.Settings.Prop.CustomCpuAffinityMask:X}";
            set
            {
                string raw = (value ?? "").Trim().Replace("0x", "").Replace("0X", "");
                ulong mask = 0;

                if (raw.Length == 0 || ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out mask))
                {
                    App.Settings.Prop.CustomCpuAffinityMask = mask;
                    OnPropertyChanged(nameof(CpuAffinityMaskText));
                }
            }
        }

        #endregion

        #region Network Diagnostics (RobloxRouteLab integration)

        public bool NetworkMonitorEnabled
        {
            get => App.Settings.Prop.CompetitiveNetworkMonitorEnabled;
            set { App.Settings.Prop.CompetitiveNetworkMonitorEnabled = value; Raise(); }
        }

        #endregion

        #region Current Session (live, polled from watcher state file)

        private System.Windows.Threading.DispatcherTimer? _sessionTimer;
        private string _lastSessionState = "";
        internal void StartSessionPolling() { PollSession(); _sessionTimer?.Start(); if (_historyPaused) { _historyPaused = false; LoadHistory(); } }
        internal void StopSessionPolling()
        {
            _sessionTimer?.Stop();
            _historyPaused = true;
            lock (_historyGate) _historyCancellation?.Cancel();
        }
        private static bool IsSessionClientRunning(CompetitiveNetworkState state)
        {
            if (state.ProcessId == 0) return true; // compatibility with older watcher state files
            try
            {
                using var client = Process.GetProcessById(state.ProcessId);
                return !client.HasExited && client.ProcessName == "RobloxPlayerBeta" && client.StartTime <= state.Timestamp;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; } // unavailable access is not proof of exit
        }

        public string SessionLocation { get; private set; } = "—";
        public string SessionQuality { get; private set; } = "—";
        public string SessionUdmux { get; private set; } = "—";
        public string SessionTeleport { get; private set; } = "—";
        public string SessionWarp { get; private set; } = "—";
        public string SessionColo { get; private set; } = "—";
        public string SessionIcmp { get; private set; } = "—";
        public string SessionUpdated { get; private set; } = "";

        // advanced / debug details
        public string SessionJobId { get; private set; } = "—";
        public string SessionRccIp { get; private set; } = "—";
        public string SessionUniversePlace { get; private set; } = "—";
        public string SessionTraceFile { get; private set; } = "";

        internal void PollSession()
        {
            if (Networking.AdaptiveRegionService.RefreshLearnedFields()) { Raise(nameof(PreferredCity)); Raise(nameof(FallbackCitiesText)); }
            _ = RefreshWarpStateAsync();
            OnPropertyChanged(nameof(SelectedCompetitivePriority)); OnPropertyChanged(nameof(FpsStatus)); OnPropertyChanged(nameof(WarpStatus)); OnPropertyChanged(nameof(WarpConnected));
            var state = CompetitiveNetworkState.TryRead();
            if (state is not null && !IsSessionClientRunning(state)) state = null;
            if (state is null)
            {
                if (_lastSessionState.Length == 0) return;
                _lastSessionState = "";
                SessionLocation = SessionQuality = SessionUdmux = SessionTeleport = SessionWarp = SessionColo = SessionIcmp = SessionJobId = SessionRccIp = SessionUniversePlace = "—";
                SessionUpdated = SessionTraceFile = "";
                foreach (string name in new[] { nameof(SessionLocation), nameof(SessionQuality), nameof(SessionUdmux), nameof(SessionTeleport), nameof(SessionWarp), nameof(SessionColo), nameof(SessionIcmp), nameof(SessionUpdated), nameof(SessionJobId), nameof(SessionRccIp), nameof(SessionUniversePlace), nameof(SessionTraceFile) }) OnPropertyChanged(name);
                return;
            }

            // Both stages retain the join timestamp; compare the full state so diagnostics appear.
            string stateKey = JsonSerializer.Serialize(state);
            if (stateKey == _lastSessionState) return;
            _lastSessionState = stateKey;

            SessionLocation = string.IsNullOrEmpty(state.Location) ? "Unknown" : state.Location;
            SessionQuality = state.RegionQuality;
            SessionUdmux = string.IsNullOrEmpty(state.UdmuxIp)
                ? "direct (no UDMUX)"
                : $"{state.UdmuxIp}:{state.UdmuxPort?.ToString() ?? "?"}";

            var tp = new List<string>();
            if (state.IsTeleport) tp.Add("teleport");
            if (state.Reserved) tp.Add("reserved");
            SessionTeleport = tp.Count > 0 ? string.Join(" + ", tp) : "public join";

            SessionWarp = state.Warp.ToUpperInvariant();
            SessionColo = string.IsNullOrEmpty(state.Colo) ? "—" : state.Colo;
            SessionIcmp = state.IcmpAvgMs is double ms
                ? $"{ms:0} ms (jitter {state.IcmpJitterMs?.ToString("0") ?? "?"})"
                : "no ICMP measurement";
            SessionUpdated = state.Timestamp.ToString("HH:mm:ss");

            SessionJobId = string.IsNullOrEmpty(state.JobId) ? "—" : state.JobId[..Math.Min(8, state.JobId.Length)] + "…";
            SessionRccIp = string.IsNullOrEmpty(state.RccIp) ? "—" : state.RccIp;
            SessionUniversePlace = $"{state.UniverseId} / {state.PlaceId}";
            SessionTraceFile = state.TracerouteFile;

            foreach (var name in new[]
            {
                nameof(SessionLocation), nameof(SessionQuality), nameof(SessionUdmux), nameof(SessionTeleport),
                nameof(SessionWarp), nameof(SessionColo), nameof(SessionIcmp), nameof(SessionUpdated),
                nameof(SessionJobId), nameof(SessionRccIp), nameof(SessionUniversePlace), nameof(SessionTraceFile)
            })
                OnPropertyChanged(name);
        }

        #endregion

        #region Recent Matches + Direct/WARP comparison (from JSONL history)

        public sealed class RecentMatchRow
        {
            public string Time { get; init; } = "";
            public string Region { get; init; } = "";
            public string Quality { get; init; } = "";
            public string Reserved { get; init; } = "—";
            public string Warp { get; init; } = "?";
            public string Icmp { get; init; } = "—";
            public string Place { get; init; } = "";
        }

        /// <summary>Per-run-label summary for the Direct vs WARP comparison page.</summary>
        public sealed class RunLabelSummary
        {
            public string Label { get; init; } = "(unlabeled)";
            public int Matches { get; set; }
            public int IdealCount { get; set; }
            public int GoodCount { get; set; }      // Excellent + Good
            public int AcceptableCount { get; set; }
            public int PoorBadCount { get; set; }
            public int UnknownCount { get; set; }
            public double? AvgIcmpMs { get; set; }

            /// <summary>Always observational wording - never "WARP causes X".</summary>
            public string ConfidenceNote => Matches switch
            {
                >= 30 => $"observed distribution, n={Matches} (decent sample)",
                >= 10 => $"observed distribution, n={Matches} (small sample - keep playing)",
                _ => $"observed distribution, n={Matches} (too few matches to conclude anything)"
            };
        }

        public ObservableCollection<RecentMatchRow> RecentMatches { get; } = new();
        public ObservableCollection<RunLabelSummary> RunSummaries { get; } = new();

        public ICommand RefreshHistoryCommand => new RelayCommand(LoadHistory);

        /// <summary>Lightweight JSONL line shape (only the fields the UI displays).</summary>
        private sealed class JsonlLineDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("runLabel")] public string RunLabel { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("placeId")] public long PlaceId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("reserved")] public bool Reserved { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("location")] public string Location { get; set; } = "";
            [System.Text.Json.Serialization.JsonPropertyName("regionQuality")] public string RegionQuality { get; set; } = "Unknown";
            [System.Text.Json.Serialization.JsonPropertyName("cloudflareWarp")] public bool? CloudflareWarp { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("icmpAvgMs")] public double? IcmpAvgMs { get; set; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions _jsonlReadOptions = new()
        {
            PropertyNamingPolicy = null,
            AllowTrailingCommas = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
        };

        /// <summary>
        /// Loads the last 50 matches (3 daily files, newest first) and rebuilds run-label summaries.
        /// Reads only the tail needed for 50 matches, off the UI thread.
        /// </summary>
        private CancellationTokenSource? _historyCancellation;
        private readonly object _historyGate = new();
        private bool _historyPaused;
        public string HistoryStatus { get; private set; } = "";
        internal Task HistoryLoadTask { get; private set; } = Task.CompletedTask;
        private void LoadHistory() => HistoryLoadTask = LoadHistoryAsync();

        private async Task LoadHistoryAsync()
        {
            var cancellation = new CancellationTokenSource();
            lock (_historyGate)
            {
                _historyCancellation?.Cancel();
                _historyCancellation = cancellation;
            }
            var token = cancellation.Token;
            HistoryStatus = "Loading recent matches…";
            OnPropertyChanged(nameof(HistoryStatus));
            try
            {
                var result = await Task.Run(() => ReadHistory(token), token);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _historyCancellation != cancellation) return;
                    RecentMatches.Clear();
                    foreach (var row in result.Rows) RecentMatches.Add(row);
                    RunSummaries.Clear();
                    foreach (var summary in result.Summaries) RunSummaries.Add(summary);
                    HistoryStatus = result.Rows.Count == 0 ? "No matches recorded in the last three days." : $"Showing {result.Rows.Count} recent matches.";
                    OnPropertyChanged(nameof(HistoryStatus));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteException("CompetitivePageViewModel::LoadHistory", ex);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || _historyCancellation != cancellation) return;
                    HistoryStatus = "History is unavailable. Existing results were kept; try Refresh again.";
                    OnPropertyChanged(nameof(HistoryStatus));
                });
            }
            finally
            {
                lock (_historyGate)
                {
                    if (_historyCancellation == cancellation) _historyCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        private static (List<RecentMatchRow> Rows, List<RunLabelSummary> Summaries) ReadHistory(CancellationToken token)
        {
                var rows = new List<RecentMatchRow>();
                var allEvents = new List<JsonlLineDto>();

                for (int daysAgo = 0; daysAgo < 3 && rows.Count < 50; daysAgo++)
                {
                    string path = Path.Combine(Paths.Logs, "CompetitiveSessions", $"{DateTime.Today.AddDays(-daysAgo):yyyy-MM-dd}.jsonl");
                    if (!File.Exists(path))
                        continue;

                    foreach (string line in ReverseLineReader.Read(path, token))
                    {
                        var evt = ParseEventLine(line);
                        if (evt is null)
                            continue;

                        allEvents.Add(evt);

                        rows.Add(new RecentMatchRow
                        {
                            Time = evt.Timestamp.ToString("HH:mm"),
                            Region = string.IsNullOrEmpty(evt.Location) ? "Unknown" : evt.Location,
                            Quality = evt.RegionQuality,
                            Reserved = evt.Reserved ? "Yes" : "—",
                            Warp = evt.CloudflareWarp is true ? "On" : evt.CloudflareWarp is false ? "Off" : "Unknown",
                            Icmp = evt.IcmpAvgMs is double ms ? $"{ms:0} ms" : "—",
                            Place = evt.PlaceId.ToString()
                        });

                        if (rows.Count >= 50)
                            break;
                    }
                }

                // --- run-label comparison (Direct vs WARP etc.) ---
                var summaries = new List<RunLabelSummary>();

                foreach (var group in allEvents.GroupBy(e =>
                    $"{(string.IsNullOrWhiteSpace(e.RunLabel) ? "(unlabeled)" : e.RunLabel)} / WARP {(e.CloudflareWarp is true ? "On" : e.CloudflareWarp is false ? "Off" : "Unknown")}"))
                {
                    double? avgIcmp = null;
                    var icmps = group.Where(e => e.IcmpAvgMs is not null).Select(e => e.IcmpAvgMs!.Value).ToList();
                    if (icmps.Count > 0)
                        avgIcmp = Math.Round(icmps.Average(), 1);

                    summaries.Add(new RunLabelSummary
                    {
                        Label = group.Key,
                        Matches = group.Count(),
                        IdealCount = group.Count(e => e.RegionQuality == "Ideal"),
                        GoodCount = group.Count(e => e.RegionQuality is "Excellent" or "Good"),
                        AcceptableCount = group.Count(e => e.RegionQuality == "Acceptable"),
                        PoorBadCount = group.Count(e => e.RegionQuality is "Poor" or "Bad"),
                        UnknownCount = group.Count(e => e.RegionQuality == "Unknown"),
                        AvgIcmpMs = avgIcmp
                    });
                }

                summaries.Sort((a, b) => b.Matches.CompareTo(a.Matches));

                return (rows, summaries);
        }

        private static JsonlLineDto? ParseEventLine(string line)
        {
            try
            {
                if (line.Length < 10 || !line.Contains("\"event\""))
                    return null;

                var dto = JsonSerializer.Deserialize<JsonlLineDto>(line, _jsonlReadOptions);
                return dto?.Type == "event" ? dto : null;
            }
            catch
            {
                return null; // one corrupt line must not kill the whole history view
            }
        }

        #endregion

        #region Presets & Log

        public ICommand ApplyBalancedPresetCommand => new RelayCommand(ApplyBalancedPreset);
        public ICommand ApplyMaximumPresetCommand => new RelayCommand(ApplyMaximumPreset);
        public ICommand ApplyDefaultRobloxPresetCommand => new RelayCommand(ApplyDefaultRobloxPreset);
        public ICommand ViewCompetitiveLogCommand => new RelayCommand(ViewCompetitiveLog);
        public ICommand OpenLogsFolderCommand => new RelayCommand(OpenLogsFolder);

        /// <summary>
        /// Priority Above Normal, performance QoS, moderate visual reductions. FPS cap stays user-chosen.
        /// </summary>
        private void ApplyBalancedPreset()
        {
            var s = App.Settings.Prop;

            s.CompetitiveModeEnabled = true;
            s.PreferredRegionEnabled = true;
            s.ChimeRegionMonitorEnabled = true;
            s.WarnOnBadChimeRegion = true;
            s.LogCompetitiveSessions = true;

            s.CompetitivePerformanceEnabled = true;
            s.CompetitiveAggressiveRendering = false;
            s.MatchFpsToMonitorRefreshRate = true;
            s.AdaptiveRegionPreferencesEnabled = true;
            s.CompetitiveNetworkMonitorEnabled = true;
            s.AutoSelectPreferredServerOnLaunch = true;
            s.CompetitiveProcessPriority = ProcessPriorityOption.AboveNormal;
            s.DisableRobloxPowerThrottling = true;
            s.ReapplyPerformanceSettings = true;

            // moderate visual reductions: grass off, keep native MSAA/quality unless user opts in
            s.CompetitiveMSAA1x = false;
            s.CompetitiveDisableGrass = true;
            s.CompetitiveLowPolyMeshes = false;
            s.CompetitiveLowGraphicsPreset = false;
            s.CompetitiveRenderer = CompetitiveRendererOption.Automatic;
            Roblox.MonitorRefreshRateService.ApplyDetectedCap();

            // affinity stays automatic
            s.CustomCpuAffinityEnabled = false;
            s.CustomCpuAffinityMask = 0;
            Networking.NetworkTestResult.Read()?.Apply(s);
            App.Settings.Save();
            NotifyAllChanged();
        }

        /// <summary>
        /// The main requested profile: High priority, performance QoS, aggressive-but-readable rendering.
        /// </summary>
        private void ApplyMaximumPreset()
        {
            var s = App.Settings.Prop;

            s.CompetitiveModeEnabled = true;
            s.PreferredRegionEnabled = true;
            s.ChimeRegionMonitorEnabled = true;
            s.WarnOnBadChimeRegion = true;
            s.LogCompetitiveSessions = true;

            s.CompetitivePerformanceEnabled = true;
            s.CompetitiveProcessPriority = ProcessPriorityOption.High; // High, NOT RealTime
            s.DisableRobloxPowerThrottling = true;
            s.ReapplyPerformanceSettings = true;

            s.CompetitiveAggressiveRendering = true;
            s.MatchFpsToMonitorRefreshRate = true;
            s.AdaptiveRegionPreferencesEnabled = true;
            s.CompetitiveNetworkMonitorEnabled = true;
            s.AutoSelectPreferredServerOnLaunch = true;
            s.CompetitiveGraphicsQualityLevel = 3;
            s.CompetitiveRenderer = CompetitiveRendererOption.Automatic;
            Roblox.MonitorRefreshRateService.ApplyDetectedCap();

            s.CompetitiveMSAA1x = true;
            s.CompetitiveDisableGrass = true;
            s.CompetitiveLowPolyMeshes = true;
            s.CompetitiveLowGraphicsPreset = true;

            s.CustomCpuAffinityEnabled = false;   // automatic / Windows managed
            s.CustomCpuAffinityMask = 0;
            Networking.NetworkTestResult.Read()?.Apply(s);
            App.Settings.Save();
            NotifyAllChanged();
        }

        /// <summary>Full revert to ordinary Froststrap behavior.</summary>
        private void ApplyDefaultRobloxPreset()
        {
            var result = Frontend.ShowMessageBox(
                "Disable all Competitive Mode modifications and return to ordinary DepthStrap behavior?\n\n" +
                "Previously saved FPS, quality and rendering values will be restored at the next launch.",
                MessageBoxImage.Question, MessageBoxButton.YesNo);

            if (result != MessageBoxResult.Yes)
                return;

            App.Settings.Prop.CompetitiveModeEnabled = false;
            App.Settings.Save();
            NotifyAllChanged();
        }

        private void ViewCompetitiveLog()
        {
            try
            {
                string path = CompetitiveSessionLogger.GetLogPath();
                if (!File.Exists(path))
                {
                    Frontend.ShowMessageBox("No competitive session log yet for today.\nIt is created on the next Roblox launch with Competitive Mode enabled.", MessageBoxImage.Information);
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox($"Failed to open log: {ex.Message}", MessageBoxImage.Warning);
            }
        }

        private void OpenLogsFolder()
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{Paths.Logs}\"", UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox($"Failed to open logs folder: {ex.Message}", MessageBoxImage.Warning);
            }
        }

        private void NotifyAllChanged()
        {
            foreach (var name in new[]
            {
                nameof(Enabled), nameof(PreferredCity), nameof(FallbackCitiesText), nameof(PreferNorthAmericaOnly), nameof(PreferEuropeOnly),
                nameof(WarpConnected), nameof(WarpStatus), nameof(IcmpAvailable), nameof(CloudflareAvailable), nameof(RegionsAvailable), nameof(TraceAvailable), nameof(FpsStatus),
                nameof(WarpCanControl),
                nameof(ManualPerformanceControls), nameof(ManualFpsEnabled), nameof(MatchMonitorRefresh), nameof(AdaptiveRegions), nameof(CalibrationStatus),
                nameof(MaxScanPages), nameof(ScanTimeoutSeconds), nameof(AutoSelectPreferredServerOnLaunch),
                nameof(ChimeMonitorEnabled), nameof(WarnOnBadChimeRegion), nameof(LogCompetitiveSessions), nameof(AutoLeaveBadChimeRegion), nameof(ManualRegionControls),
                nameof(SelectedCompetitivePriority), nameof(ShowRealtimeWarning), nameof(DisablePowerThrottling),
                nameof(ReapplyPerformanceSettings), nameof(ReapplyDelayMs), nameof(SelectedFpsPreset), nameof(CustomFpsCapText),
                nameof(LowGraphicsPreset), nameof(GraphicsQualityLevel),
                nameof(MSAA1x), nameof(DisableGrass), nameof(LowPolyMeshes), nameof(SelectedRenderer),
                nameof(SelectedAffinityMode), nameof(CpuAffinityMaskText),
                nameof(NetworkMonitorEnabled)
            })
                OnPropertyChanged(name);
        }

        #endregion
    }
}
