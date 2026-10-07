using System.Collections.ObjectModel;

namespace Bloxstrap.Models.Persistable
{
    public class Settings : IJsonNormalizable
    {

        // Integration Page
        public bool EnableActivityTracking { get; set; } = true;
        public bool ShowServerDetails { get; set; } = true;
        public bool ShowServerUptime { get; set; } = false;
        public bool AutoRejoin { get; set; } = false;
        public bool ShowGameHistoryMenu { get; set; } = true;
        public bool PlaytimeCounter { get; set; } = true;
        public TrayDoubleClickAction DoubleClickAction { get; set; } = TrayDoubleClickAction.ServerInfo;
        public bool UseDisableAppPatch { get; set; } = false;
        public bool ShowUsingFroststrapRPC { get; set; } = true;
        public bool UseDiscordRichPresence { get; set; } = true;
        public bool HideRPCButtons { get; set; } = true;
        public bool EnableCustomStatusDisplay { get; set; } = true;
        public bool ShowAccountOnRichPresence { get; set; } = false;
        public bool StudioRPC { get; set; } = false;
        public bool StudioThumbnailChanging { get; set; } = false;
        public bool StudioEditingInfo { get; set; } = false;
        public bool StudioWorkspaceInfo { get; set; } = false;
        public bool StudioShowTesting { get; set; } = false;
        public bool StudioGameButton { get; set; } = false;
        public ObservableCollection<CustomIntegration> CustomIntegrations { get; set; } = new();

        // Bootstrapper Page
        public bool ConfirmLaunches { get; set; } = true;

        public bool AutoCloseCrashHandler { get; set; } = false;
        public CleanerOptions CleanerOptions { get; set; } = CleanerOptions.Never;
        public List<string> CleanerDirectories { get; set; } = new List<string>();
        public bool PauseRobloxUpdates { get; set; } = true;
        public bool BackgroundUpdatesEnabled { get; set; } = false;
        public bool MultiInstanceLaunching { get; set; } = false;
        public int MultibloxInstanceCount { get; set; } = 2;
        public int MultibloxDelayMs { get; set; } = 1500;
        public ProcessPriorityOption SelectedProcessPriority { get; set; } = ProcessPriorityOption.Normal;

        // FastFlag Editor/Settings Related
        public bool UseFastFlagManager { get; set; } = true;
        public bool ShowPresetColumn { get; set; } = false;
        public bool ShowFlagCount { get; set; } = true;
        public bool UseAltManually { get; set; } = true;

        // Appearance Page
        public BootstrapperStyle BootstrapperStyle { get; set; } = BootstrapperStyle.DepthStrapDialog;
        public BootstrapperIcon BootstrapperIcon { get; set; } = BootstrapperIcon.IconDepthStrap;
        public int AppearanceVersion { get; set; } = 0;
        public WindowsBackdrops SelectedBackdrop { get; set; } = WindowsBackdrops.None;
        public string Locale { get; set; } = "nil";
        public string? SelectedCustomTheme { get; set; } = null;
        public List<GradientStops> CustomGradientStops { get; set; } = new()
        {
            new GradientStops { Offset = 0.0, Color = "#4D5560" },
            new GradientStops { Offset = 0.5, Color = "#383F47" },
            new GradientStops { Offset = 1.0, Color = "#252A30" }
        };
        public double GradientAngle { get; set; } = 0;
        public BackgroundMode BackgroundType { get; set; } = BackgroundMode.Gradient;
        public string? BackgroundImagePath { get; set; }
        public BackgroundStretch BackgroundStretch { get; set; } = BackgroundStretch.UniformToFill;
        public double BackgroundOpacity { get; set; } = 1.0;
        /// <summary>Title shown on the bootstrapper dialog. Defaults to the fork display name.</summary>
        public string BootstrapperTitle { get; set; } = App.DisplayName;
        public string BootstrapperIconCustomLocation { get; set; } = "";
        public string DownloadingStringFormat { get; set; } = Strings.Bootstrapper_Status_Downloading + " {0} - {1}MB / {2}MB";
        public Theme Theme { get; set; } = Theme.CrimsonContract;
        public string? CustomFontPath { get; set; } = null;

        // Settings Page
        public UpdateCheck UpdateChecks { get; set; } = UpdateCheck.Disabled;
        public bool SaveAndLaunchToPlayer { get; set; } = true;
        public bool WPFSoftwareRender { get; set; } = false;
        public bool UpdateRoblox { get; set; } = true;
        public string RobloxPlayerVersionOverride { get; set; } = "";
        public int RobloxVersionArchiveLimit { get; set; } = 5;
        public string RobloxDomain { get; set; } = RobloxInterfaces.Deployment.DefaultRobloxDomain;
        public bool StaticDirectory { get; set; } = false;
        public string Channel { get; set; } = RobloxInterfaces.Deployment.DefaultChannel;
        public ChannelChangeMode ChannelChangeMode { get; set; } = ChannelChangeMode.Prompt;

        // Competitive / Region Optimization
        public bool CompetitiveModeEnabled { get; set; } = true;

        public bool PreferredRegionEnabled { get; set; } = true;

        /// <summary>
        /// Learned during installation; no region is preselected for a new user.
        /// Matched against datacenter city names via aliases, not exact display strings.
        /// </summary>
        public string CompetitivePreferredCity { get; set; } = "";

        /// <summary>
        /// Ordered fallback cities. First entry is the strongest fallback.
        /// </summary>
        public List<string> CompetitiveFallbackCities { get; set; } = new();

        public bool PreferEuropeOnly { get; set; } = false;
        public bool AutomaticRegionalPreference { get; set; } = true;
        public bool PreferNorthAmericaOnly { get; set; } = false;
        public bool AdaptiveRegionPreferencesEnabled { get; set; } = true;
        public bool CloudflareTermsAccepted { get; set; } = false;
        public int NetworkSetupVersion { get; set; } = 0;
        public bool RegionCalibrationCompleted { get; set; } = false;
        public bool MatchFpsToMonitorRefreshRate { get; set; } = true;

        /// <summary>
        /// Bounded preferred-region scan: max pages of public servers to inspect.
        /// </summary>
        public int PreferredRegionMaxPages { get; set; } = 10;

        /// <summary>
        /// Bounded preferred-region scan: wall-clock limit in seconds.
        /// </summary>
        public int PreferredRegionSearchTimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// Phase-2 feature: when a plain Deepwoken launch URI arrives without an explicit
        /// gameInstanceId, search public servers and pick the best region candidate.
        /// </summary>
        public bool AutoSelectPreferredServerOnLaunch { get; set; } = true;

        // Competitive / Chime Region Monitor
        public bool ChimeRegionMonitorEnabled { get; set; } = true;
        public bool WarnOnBadChimeRegion { get; set; } = true;
        public int RegionMonitoringVersion { get; set; } = 0;
        public bool LogCompetitiveSessions { get; set; } = true;

        /// <summary>
        /// ADVANCED / EXPERIMENTAL. Default OFF: a bad-region Chime server may already count as a match.
        /// Auto-leaving can cause losses, penalties or ELO impact. Warn + log only by default.
        /// </summary>
        public bool AutoLeaveBadChimeRegion { get; set; } = false;

        // Competitive / Network Diagnostics (RobloxRouteLab integration)
        /// <summary>Master switch for the live network monitor (UDMUX capture, region, WARP state, JSONL history).</summary>
        public bool CompetitiveNetworkMonitorEnabled { get; set; } = true;

        /// <summary>
        /// Research mode: measure ICMP on every join, refresh Cloudflare trace per join and keep extra metadata.
        /// Normal use (disabled) still does region lookup + Chime notifications + light history logging.
        /// </summary>
        public bool CompetitiveExperimentMode { get; set; } = false;

        /// <summary>Background tracert on each join, saved to Logs/CompetitiveRoutes/. Default OFF (traffic + time cost).</summary>
        public bool CompetitiveTracerouteEnabled { get; set; } = false;

        /// <summary>Supplementary ICMP RTT against the live endpoint. Received=0 means "no measurement", not 100% loss.</summary>
        public bool CompetitiveIcmpEnabled { get; set; } = true;

        /// <summary>Query cdn-cgi/trace for WARP state + ingress POP (cached, max once per 30s).</summary>
        public bool CompetitiveCloudflareDetectionEnabled { get; set; } = true;

        /// <summary>A/B experiment label written into every log line (Direct / WARP / ...). Never trusted as the actual WARP state.</summary>
        public string CompetitiveRunLabel { get; set; } = "";

        /// <summary>Privacy: store the full public egress IP in network logs. Default stores masked form (123.45.xxx.xxx).</summary>
        public bool StoreFullEgressIpInLogs { get; set; } = false;

        // DepthStrap feature strip (stage 1): legacy Froststrap integrations are dormant by default.
        /// <summary>
        /// Re-enables legacy Froststrap integrations that were stripped from the UI:
        /// Discord Rich Presence (player + studio) and the "using DepthStrap" status RPC.
        /// Their individual toggles still apply on top of this master gate.
        /// </summary>
        public bool LegacyIntegrationsEnabled { get; set; } = false;

        // Competitive / Performance
        public bool CompetitivePerformanceEnabled { get; set; } = true;

        /// <summary>
        /// High is the recommended competitive default. RealTime is allowed but warned about.
        /// </summary>
        public ProcessPriorityOption CompetitiveProcessPriority { get; set; } = ProcessPriorityOption.High;

        /// <summary>
        /// Requests performance-oriented (HighQoS-style) execution-speed power throttling on the Roblox process only.
       /// </summary>
        public bool DisableRobloxPowerThrottling { get; set; } = true;

        public bool ReapplyPerformanceSettings { get; set; } = true;
        public int PerformanceReapplyDelayMs { get; set; } = 3000;

        /// <summary>
        /// Applied through Roblox GlobalBasicSettings (FramerateCap), not the obsolete DFIntTaskSchedulerTargetFps flag.
        /// 0 means uncapped / Roblox maximum.
        /// </summary>
        public int CompetitiveFpsCap { get; set; } = 0;

        /// <summary>
        /// Lowers SavedQualityLevel in GlobalBasicSettings to CompetitiveGraphicsQualityLevel when the current level is higher.
        /// Never raises quality.
        /// </summary>
        public bool CompetitiveLowGraphicsPreset { get; set; } = true;
        public int CompetitiveGraphicsQualityLevel { get; set; } = 3;
        public bool CompetitiveAggressiveRendering { get; set; } = true;

        // Competitive / Rendering (FastFlag layer - best effort, effectiveness depends on Roblox allowlist)
        public bool CompetitiveMSAA1x { get; set; } = true;
        public bool CompetitiveDisableGrass { get; set; } = true;
        public bool CompetitiveLowPolyMeshes { get; set; } = true;
        public CompetitiveRendererOption CompetitiveRenderer { get; set; } = CompetitiveRendererOption.Automatic;

        // Competitive / Advanced (unsafe-ish, opt-in)
        /// <summary>
        /// Default OFF: Windows normally knows the machine topology better than a bootstrapper.
        /// </summary>
        public bool CustomCpuAffinityEnabled { get; set; } = false;

        /// <summary>
        /// Bitmask of logical processors. 0 / unset means system-managed (all cores).
        /// </summary>
        public ulong CustomCpuAffinityMask { get; set; } = 0;

        // Misc Stuff
        public bool IsNavigationSidebarExpanded { get; set; } = true;
        public string SelectedRegion { get; set; } = string.Empty;
        public bool ForceLocalData { get; set; } = false;
        public bool DebugDisableVersionPackageCleanup { get; set; } = false;

        void IJsonNormalizable.Normalize()
        {
            var defaults = new Settings();
            // JSON may explicitly contain null despite non-nullable property declarations.
            foreach (var property in typeof(Settings).GetProperties())
            {
                var value = property.GetValue(this);
                if (value is null && property.GetValue(defaults) is object fallback)
                    property.SetValue(this, fallback);
                else if (property.PropertyType.IsEnum && value is not null && !Enum.IsDefined(property.PropertyType, value))
                    property.SetValue(this, property.GetValue(defaults));
            }
            CompetitiveFallbackCities = CompetitiveFallbackCities.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CleanerDirectories = CleanerDirectories.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            CustomIntegrations = new(CustomIntegrations.Where(x => x is not null));
            CustomGradientStops = CustomGradientStops.Where(x => x is not null).ToList();
            foreach (var stop in CustomGradientStops) stop.Color ??= "#000000";
            foreach (var integration in CustomIntegrations)
            {
                integration.Name ??= ""; integration.Location ??= "";
                integration.LaunchArgs ??= ""; integration.GameID ??= "";
            }
        }
    }
}
