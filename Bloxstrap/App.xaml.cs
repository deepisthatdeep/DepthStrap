using Bloxstrap.Integrations;
using Microsoft.Win32;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Hardware;

namespace Bloxstrap
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
#if QA_BUILD
        public const string ProjectName = "DepthStrap-QA";
#else
        public const string ProjectName = "DepthStrap";
#endif

        /// <summary>
        /// User-visible product name for this fork.
        /// Internal namespaces stay compatible with v1.5.1; install identity belongs to this fork.
        /// </summary>
#if QA_BUILD
        public const string DisplayName = "DepthStrap-QA";
#else
        public const string DisplayName = "DepthStrap";
#endif
        public const string ProjectOwner = "DepthStrap";
        // App updates use only stable releases from the DepthStrap repository.
#if DEBUG || QA_BUILD
        public static bool SupportsAppUpdates => false;
#else
        public static bool SupportsAppUpdates => true;
#endif
        public const string ProjectRepository = "deepisthatdeep/DepthStrap";
        public const string ProjectDownloadLink = "https://github.com/deepisthatdeep/DepthStrap/releases";
        public const string ProjectHelpLink = "https://en.help.roblox.com/";
        public const string ProjectSupportLink = "https://github.com/deepisthatdeep/DepthStrap/issues";
        public const string ProjectRemoteDataLink = "";
        public static bool SupportsLegacyIntegrations => false;

        public const string RobloxPlayerAppName = "RobloxPlayerBeta.exe";
        public const string RobloxStudioAppName = "RobloxStudioBeta.exe";

        // simple shorthand for extremely frequently used and long string - this goes under HKCU
        public const string UninstallKey = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProjectName}";

        public const string ApisKey = $"Software\\{ProjectName}";
        public static LaunchSettings LaunchSettings { get; private set; } = null!;

        public static BuildMetadataAttribute BuildMetadata = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildMetadataAttribute>()!;

        public static string Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString()[..^2];

        public static Bootstrapper? Bootstrapper { get; set; } = null!;

        public FroststrapRichPresence RichPresence { get; private set; } = null!;

        public static bool IsActionBuild => !String.IsNullOrEmpty(BuildMetadata.CommitRef);

        public static bool IsProductionBuild => IsActionBuild && BuildMetadata.CommitRef.StartsWith("tag", StringComparison.Ordinal);

        public static bool IsPlayerInstalled => App.PlayerState.IsSaved && !String.IsNullOrEmpty(App.PlayerState.Prop.VersionGuid);

        public static bool IsStudioInstalled => App.StudioState.IsSaved && !String.IsNullOrEmpty(App.StudioState.Prop.VersionGuid);

        public static readonly MD5 MD5Provider = MD5.Create();

        public static readonly Logger Logger = new();

        public static readonly Dictionary<string, BaseTask> PendingSettingTasks = new();

        // Disambiguate Settings so we use the persistable Settings (Bloxstrap.Models.Persistable.Settings),
        // not the auto-generated Properties.Settings which doesn't contain the clicker fields.
        public static readonly JsonManager<Settings> Settings = new();

        public static readonly JsonManager<State> State = new();

        public static readonly LazyJsonManager<DistributionState> PlayerState = new(nameof(PlayerState));

        public static readonly LazyJsonManager<DistributionState> StudioState = new(nameof(StudioState));

        public static readonly RemoteDataManager RemoteData = new();

        public static readonly FastFlagManager FastFlags = new();

        public static readonly GBSEditor GlobalSettings = new();

        public static readonly CookiesManager Cookies = new();

        public static readonly HttpClient HttpClient = new(new HttpClientLoggingHandler(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }));


        private static bool _showingExceptionDialog = false;

        public static void Terminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::Terminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            Environment.Exit(exitCodeNum);
        }

        public static void SoftTerminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
        {
            int exitCodeNum = (int)exitCode;

            Logger.WriteLine("App::SoftTerminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");

            Current.Dispatcher.Invoke(() => Current.Shutdown(exitCodeNum));
        }

        void GlobalExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            Logger.WriteLine("App::GlobalExceptionHandler", "An exception occurred");

            FinalizeExceptionHandling(e.Exception);
        }

        public static void FinalizeExceptionHandling(AggregateException ex)
        {
            foreach (var innerEx in ex.InnerExceptions)
                Logger.WriteException("App::FinalizeExceptionHandling", innerEx);

            FinalizeExceptionHandling(ex.GetBaseException(), false);
        }

        public static void FinalizeExceptionHandling(Exception ex, bool log = true)
        {
            if (log)
                Logger.WriteException("App::FinalizeExceptionHandling", ex);

            if (AppUpdater.CanResumeFailedUpdate(LaunchSettings) && AppUpdater.TryResumeInstalled())
            {
                Logger.WriteLine("App::FinalizeExceptionHandling", "Failed app updater resumed the installed app once.");
                Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                return;
            }

            if (_showingExceptionDialog)
                return;

            _showingExceptionDialog = true;

            if (Bootstrapper?.Dialog != null)
            {
                if (Bootstrapper.Dialog.TaskbarProgressValue == 0)
                    Bootstrapper.Dialog.TaskbarProgressValue = 1; // make sure it's visible

                Bootstrapper.Dialog.TaskbarProgressState = TaskbarItemProgressState.Error;
            }

            Frontend.ShowExceptionDialog(ex);

            Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
        }

        public static FroststrapRichPresence? FrostRPC
        {
            get => (Current as App)?.RichPresence;
            set
            {
                if (Current is App app)
                    app.RichPresence = value!;
            }
        }

        public static void WindowsBackdrop()
        {
            Current.Dispatcher.Invoke(() =>
            {
                var backdropType = Settings.Prop.SelectedBackdrop;
                ApplyBackdropToAllWindows(backdropType);
            });
        }

        private static void ApplyBackdropToAllWindows(WindowsBackdrops backdropType)
        {
            foreach (Window window in Current.Windows)
                if (window is UiWindow uiWindow) ApplyWindowBackdrop(uiWindow, backdropType);
        }

        internal static void ApplyWindowBackdrop(UiWindow uiWindow, WindowsBackdrops backdropType)
        {
            // Native-title-bar dialogs keep their themed content surface; this
            // WPF UI backdrop implementation requires an extended client area.
            if (!uiWindow.ExtendsContentIntoTitleBar)
            {
                uiWindow.WindowBackdropType = BackgroundType.None;
                return;
            }
            var wpfBackdrop = backdropType switch
            {
                WindowsBackdrops.None => BackgroundType.None,
                WindowsBackdrops.Mica => BackgroundType.Mica,
                WindowsBackdrops.Acrylic => BackgroundType.Acrylic,
                WindowsBackdrops.Aero => BackgroundType.Aero,
                _ => BackgroundType.None
            };

            bool transparent = wpfBackdrop is BackgroundType.Acrylic or BackgroundType.Aero;
            bool initialized = new WindowInteropHelper(uiWindow).Handle != IntPtr.Zero;
            if (initialized && uiWindow.AllowsTransparency != transparent)
            {
                // WPF cannot change its composition mode after a native handle
                // exists. The saved choice takes effect on the next window.
                Logger.WriteLine("Appearance", "Backdrop composition change deferred until this window is reopened.");
                uiWindow.WindowBackdropType = BackgroundType.None;
                return;
            }
            if (!initialized)
            {
                if (transparent) uiWindow.WindowStyle = WindowStyle.None;
                uiWindow.AllowsTransparency = transparent;
                if (!transparent) uiWindow.WindowStyle = WindowStyle.SingleBorderWindow;
            }
            uiWindow.WindowBackdropType = wpfBackdrop;
        }

        public void ApplyCustomFontToWindow(Window window) => FontManager.ApplySavedFont(window);

        public static void AssertWindowsOSVersion()
        {
            const string LOG_IDENT = "App::AssertWindowsOSVersion";

            int major = Environment.OSVersion.Version.Major;
            if (major < 10) // Windows 10 and newer only
            {
                Logger.WriteLine(LOG_IDENT, $"Detected unsupported Windows version ({Environment.OSVersion.Version}).");

                if (!LaunchSettings.QuietFlag.Active)
                    Frontend.ShowMessageBox(Strings.App_OSDeprecation_Win7_81, MessageBoxImage.Error);

                Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // This installed app also hosts the explicitly elevated MAC action.
            // Dispatch before any launcher, installer or normal settings work.
            if (e.Args.Length > 0 && e.Args[0] is "--mac-apply" or "--mac-restore")
            {
                DepthStrap.Recovery.MacControls.RunHelper(e.Args);
                Shutdown(Environment.ExitCode);
                return;
            }
            const string LOG_IDENT = "App::OnStartup";

            Locale.Initialize();

            base.OnStartup(e);

            bool fontApplied = FontManager.ApplySavedCustomFont();

            if (fontApplied)
                Logger.WriteLine(LOG_IDENT, "Custom font applied at startup.");

            foreach (Window window in Application.Current.Windows)
            {
                ApplyCustomFontToWindow(window);
            }

            Logger.WriteLine(LOG_IDENT, $"Starting {ProjectName} v{Version}");

            var userAgent = new StringBuilder($"{ProjectName}/{Version}");

            if (IsActionBuild)
            {
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from commit {BuildMetadata.CommitHash} ({BuildMetadata.CommitRef})");

                if (IsProductionBuild)
                    userAgent.Append(" (Production)");
                else
                    userAgent.Append($" (Artifact {BuildMetadata.CommitHash}, {BuildMetadata.CommitRef})");
            }
            else
            {
                Logger.WriteLine(LOG_IDENT, $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from {BuildMetadata.Machine}");

#if QA_BUILD
                userAgent.Append(" (QA)");
#else
                userAgent.Append($" (Build {Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildMetadata.Machine))})");
#endif
            }

            Logger.WriteLine(LOG_IDENT, $"OSVersion: {Environment.OSVersion}");
            Logger.WriteLine(LOG_IDENT, $"Loaded from {Paths.Process}");
            Logger.WriteLine(LOG_IDENT, $"Temp path is {Paths.Temp}");
            Logger.WriteLine(LOG_IDENT, $"WindowsStartMenu path is {Paths.WindowsStartMenu}");

            ApplicationConfiguration.Initialize();

            HttpClient.Timeout = TimeSpan.FromSeconds(60);

            if (!HttpClient.DefaultRequestHeaders.UserAgent.Any())
                HttpClient.DefaultRequestHeaders.Add("User-Agent", userAgent.ToString());

            LaunchSettings = new LaunchSettings(e.Args);

            using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallKey);
            string? installLocation = null;
            bool fixInstallLocation = false;

            if (uninstallKey?.GetValue("InstallLocation") is string installLocValue)
            {
                if (Directory.Exists(installLocValue))
                {
                    installLocation = installLocValue;
                }
                else
                {
                    var match = Regex.Match(installLocValue, @"^[a-zA-Z]:\\Users\\([^\\]+)", RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        string newLocation = installLocValue.Replace(match.Value, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase);

                        if (Directory.Exists(newLocation))
                        {
                            installLocation = newLocation;
                            fixInstallLocation = true;
                        }
                    }
                }
            }

            if (installLocation == null && Directory.GetParent(Paths.Process)?.FullName is string processDir)
            {
                var files = Directory.GetFiles(processDir).Select(Path.GetFileName).ToArray();

                if (files.Length <= 3 && files.Contains("Settings.json") && files.Contains("State.json"))
                {
                    installLocation = processDir;
                    fixInstallLocation = true;
                }
            }

            if (fixInstallLocation && installLocation != null)
            {
                var installer = new Installer
                {
                    InstallLocation = installLocation,
                    IsImplicitInstall = true
                };

                if (installer.CheckInstallLocation())
                {
                    Logger.WriteLine(LOG_IDENT, $"Changing install location to '{installLocation}'");
                    installer.DoInstall();
                }
                else
                {
                    installLocation = null; // force reinstall
                }
            }

            if (installLocation == null)
            {
                Logger.Initialize(true);
                AssertWindowsOSVersion();
                Logger.WriteLine(LOG_IDENT, "Not installed, launching the installer");
                AssertWindowsOSVersion();
                LaunchHandler.LaunchInstaller();
            }
            else
            {
                Paths.Initialize(installLocation);

                if (Paths.Process != Paths.Application && !File.Exists(Paths.Application))
                    File.Copy(Paths.Process, Paths.Application);

                Logger.Initialize(LaunchSettings.UninstallFlag.Active);

                if (!Logger.Initialized && !Logger.NoWriteMode)
                {
                    Logger.WriteLine(LOG_IDENT, "Unable to initialize the diagnostic log, terminating.");
                    Terminate();
                }

                _ = Task.Run(RemoteData.LoadData); // ok

                Settings.Load();
                if (Competitive.RegionMonitoringPolicy.RepairLegacySetup(Settings.Prop, Networking.NetworkTestResult.Read()))
                    Settings.Save();
                if (Settings.Prop.AppearanceVersion < 1)
                {
                    BrandTheme.Migrate(Settings.Prop);
                    Settings.Save();
                }
                State.Load();
                FastFlags.Load();
                Roblox.CompetitiveSettingsBackup.ReleaseQualityLock();
                GlobalSettings.Load();

                // to fix error System.IO.IOException: No se encuentra el recurso 'ui/style/.xaml'.
                // when i put in installer dosent work
                // if i try to fix in wpfuiwindow also dosent work
                if (Settings.Prop.Theme > Enums.Theme.Custom)
                {
                    Settings.Prop.Theme = Enums.Theme.Dark;
                    Settings.Save();
                }



                if (!Locale.SupportedLocales.ContainsKey(Settings.Prop.Locale))
                {
                    Settings.Prop.Locale = "nil";
                    Settings.Save();
                }

                Locale.Set(Settings.Prop.Locale);

                if (LaunchSettings.UpdateHandoffFlag.Active &&
                    (!LaunchSettings.UpgradeFlag.Active || !AppUpdateHandoff.Accept(LaunchSettings.UpdateHandoffFlag.Data)))
                {
                    Logger.WriteLine(LOG_IDENT, "App update handoff was cancelled or could not be acknowledged.");
                    Terminate();
                    return;
                }

                if (!LaunchSettings.BypassUpdateCheck)
                {
                    Installer.HandleUpgrade();
                    if (LaunchSettings.UpgradeFlag.Active && !AppUpdater.IsInstalledVersion(Paths.Application, Version))
                    {
                        if (AppUpdater.CanResumeFailedUpdate(LaunchSettings) && AppUpdater.TryResumeInstalled())
                        {
                            Logger.WriteLine(LOG_IDENT, "App update deferred; resumed the installed app with its original launch action.");
                            Terminate();
                            return;
                        }
                        Frontend.ShowMessageBox("DepthStrap could not finish the app update. Close other DepthStrap windows and try again. Your previous installed version was preserved.", MessageBoxImage.Warning);
                        Terminate();
                        return;
                    }
                }

                WindowsRegistry.RegisterApis();

                if (Settings.Prop.NetworkSetupVersion < 1 && !LaunchSettings.QuietFlag.Active && !LaunchSettings.UninstallFlag.Active && !LaunchSettings.WatcherFlag.Active && !LaunchSettings.MultiInstanceWatcherFlag.Active && !LaunchSettings.BackgroundUpdaterFlag.Active)
                    new UI.Elements.Dialogs.RegionCalibrationDialog().ShowDialog();

                if (await AppUpdater.TryUpdateAsync())
                {
                    Terminate();
                    return;
                }
                LaunchHandler.ProcessLaunchArgs();
            }

        }

        protected override void OnExit(ExitEventArgs e)
        {
            FrostRPC?.Dispose();
            base.OnExit(e);
        }
    }
}
