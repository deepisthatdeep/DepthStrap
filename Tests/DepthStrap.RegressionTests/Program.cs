using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.Models;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Networking;
using Bloxstrap.Roblox;
using Bloxstrap.RobloxInterfaces;
using Bloxstrap.Competitive;
using Bloxstrap.Integrations;
using Bloxstrap.UI;
using Bloxstrap.UI.Elements.Settings;
using Bloxstrap.UI.Elements.Settings.Pages;
using Bloxstrap.UI.Elements.Bootstrapper;
using Bloxstrap.Utility;

internal static class Program
{
    private sealed class BindingErrors : TraceListener
    {
        internal readonly List<string> Messages = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
    }
    private static int _checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
    }
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--live-version-audit")
        {
            LiveVersionChecks.RunAsync(args[1]).GetAwaiter().GetResult(); return;
        }
        if (args.Length == 2 && args[0] == "--release-options")
        {
            var catalog = RobloxReleaseLookup.GetReleaseCatalogAsync().GetAwaiter().GetResult();
            var matches = RobloxReleaseLookup.MatchReleases(catalog, args[1]);
            Console.WriteLine($"Player release options for {args[1]}: {string.Join(", ", matches)}");
            if (matches.Length == 0) Environment.ExitCode = 1;
            return;
        }
        if (args.Length == 3 && args[0] == "--resolve-release")
        {
            Paths.Initialize(Path.Combine(Path.GetFullPath(args[2]), "lookup-fixture-" + Guid.NewGuid().ToString("N")));
            typeof(Paths).GetProperty("Roblox")!.SetValue(null, Path.Combine(Paths.Base, "Roblox"));
            try { Console.WriteLine(args[1] + " => " + RobloxReleaseLookup.ResolveAsync(args[1], CancellationToken.None).GetAwaiter().GetResult()); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--session-file-checks")
        {
            Paths.Initialize(Path.GetFullPath(args[1]));
            SessionStateFileChecks.Run(Check);
            Console.WriteLine($"PASS: {_checks} session-file checks"); return;
        }
        if (args.Length == 2 && args[0] == "--logger-checks")
        {
            Paths.Initialize(Path.Combine(Path.GetFullPath(args[1]), "logger-fault-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Paths.Base);
            LoggerChecks.Run(Check);
            Console.WriteLine($"PASS: {_checks} logger storage-failure checks"); return;
        }
        if (args.Length == 4 && args[0] == "--update-handoff-worker")
        {
            if (args[3] == "exit") return;
            if (args[3] == "stall") { Thread.Sleep(10000); return; }
            var forwarded = JsonSerializer.Deserialize<string[]>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[2])))!;
            var launch = new LaunchSettings(forwarded);
            bool accepted = AppUpdateHandoff.Accept(launch.UpdateHandoffFlag.Data, TimeSpan.FromMilliseconds(args[3] == "orphan" ? 200 : 2000));
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { Accepted = accepted, Args = launch.RobloxLaunchArgs }));
            return;
        }
        if (args.Length == 3 && args[0] == "--update-launch-worker")
        {
            var forwarded = JsonSerializer.Deserialize<string[]>(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[2])))!;
            var launch = new LaunchSettings(forwarded);
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { Upgrade = launch.UpgradeFlag.Active, Args = launch.RobloxLaunchArgs }));
            return;
        }
        if (args.Length == 3 && args[0] == "--convert-fastflags")
        {
            var import = FastFlagImport.ParseDetailed(File.ReadAllText(args[1]));
            File.WriteAllText(args[2], JsonSerializer.Serialize(import.Flags, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Preserved file: {import.Flags.Count} flags; {import.UnprefixedNames.Count} names without recognized prefixes; {import.SkippedNulls} null values skipped.");
            Console.WriteLine(string.Join(", ", import.UnprefixedNames));
            return;
        }
        if (args.Length == 4 && args[0] == "--backup-worker")
        {
            Paths.Initialize(args[1]);
            long target = long.Parse(args[3]);
            while (DateTime.UtcNow.Ticks < target) Thread.Sleep(5);
            for (int i = 0; i < 20; i++) CompetitiveSettingsBackup.SetFlag(args[2], i.ToString());
            return;
        }
        if (args.Length == 3 && args[0] == "--startup-log-worker")
        {
            Paths.Initialize(args[1]);
            long target = long.Parse(args[2]);
            while (DateTime.UtcNow.Ticks < target) Thread.Sleep(5);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            App.Logger.Initialize();
            Parallel.For(0, 64, i => App.Logger.WriteLine("LoggerExitFixture", $"entry-{i:D2}: {new string('x', 512)}"));
            App.Logger.WriteLine("LoggerExitFixture", "terminal-entry-before-immediate-exit");
            Environment.Exit(App.Logger.Initialized ? 0 : 2);
            return;
        }
        if (args.Length == 3 && args[0] == "--log-worker")
        {
            Paths.Initialize(args[1]);
            App.Settings.Prop = new Settings { AdaptiveRegionPreferencesEnabled = false };
            Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
            {
                await CompetitiveSessionLogger.WriteNetworkEventAsync(new CompetitiveNetworkEvent { JobId = args[2] + "-" + i, Timestamp = DateTime.Now });
                await AdaptiveRegionService.RecordAsync(new[] { new RegionObservation { City = "Fixture " + args[2], Route = "direct", AverageMs = 20 } }, "direct", CancellationToken.None);
            })).GetAwaiter().GetResult();
            foreach (string line in App.Logger.History.Where(x => x.Contains("AdaptiveRegionService"))) Console.WriteLine(line);
            return;
        }
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, typeof(App).Assembly);
        try { Render(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            foreach (var diagnostic in App.Logger.History.Where(x => x.Contains("Exception") || x.Contains("0x") || x.Contains("Failed")))
                Console.Error.WriteLine(diagnostic);
            Environment.Exit(1);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Render(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Paths.Initialize(Path.Combine(Path.GetDirectoryName(output)!, "test-data-" + Guid.NewGuid().ToString("N")));
        typeof(Paths).GetProperty("Roblox")!.SetValue(null, Path.Combine(Paths.Base, "Roblox"));
        CompetitiveSettingsBackup.PlayerPresence = () => false;
        typeof(App).GetProperty("LaunchSettings")!.SetValue(null, new LaunchSettings(Array.Empty<string>()));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = System.Xml.Linq.XDocument.Load("Bloxstrap/App.xaml");
        var element = document.Root!.Elements().First().Elements().First();
        foreach (var converter in element.Elements().Where(x => x.Name.NamespaceName.StartsWith("clr-namespace:")).ToList()) converter.Remove();
        foreach (var attribute in document.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
            if (element.Attribute(attribute.Name) is null) element.SetAttributeValue(attribute.Name, attribute.Value.Replace("clr-namespace:Bloxstrap.UI.Converters", "clr-namespace:Bloxstrap.UI.Converters;assembly=DepthStrap"));
        string resourceXml = element.ToString().Replace("clr-namespace:Bloxstrap.UI.Converters\"", "clr-namespace:Bloxstrap.UI.Converters;assembly=DepthStrap\"").Replace("Source=\"UI/Style/", "Source=\"pack://application:,,,/DepthStrap;component/UI/Style/");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resourceXml);
        foreach (string name in new[] { "StringFormatConverter", "RangeConverter", "EnumNameConverter", "BooleanToVisibilityConverter", "InverseBooleanToVisibilityConverter", "NumberAbbreviationConverter" })
            app.Resources[name] = Activator.CreateInstance(typeof(App).Assembly.GetType("Bloxstrap.UI.Converters." + name)!, true)!;
        var errorMethod = typeof(App).GetMethod("GlobalExceptionHandler", BindingFlags.NonPublic | BindingFlags.Instance)!;
        
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.Exit(1); };
        Console.WriteLine("Resources loaded");
        if (args.Contains("--autocomplete-only"))
        {
            BrandTheme.Apply(app.Resources, Theme.CrimsonContract);
            CheckReleaseDropdown(new AdvancedRobloxSettingsPage());
            Console.WriteLine($"PASS: {_checks} actual autocomplete dropdown checks.");
            return;
        }
        if (args.Contains("--update-only"))
        {
            AppUpdateChecks.Run(Check); AppUpdateFlowChecks.Run(Check); AppUpdateHandoffChecks.Run(Check);
            Console.WriteLine($"PASS: {_checks} updater checks"); return;
        }
        if (args.Contains("--audit-only")) { ReliabilityAuditChecks.Run(Check); Console.WriteLine($"PASS: {_checks} audit checks"); return; }
        if (args.Contains("--public-network-only")) { ServerBrowserChecks.VerifyPublicNetwork(Check); Console.WriteLine($"PASS: {_checks} public network checks"); return; }
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        if (args.Contains("--popup-only"))
        {
            VerifyThemes(app);
            PopupChecks.Run(Check, Path.GetDirectoryName(output)!, SaveVisual);
            Check(bindingErrors.Messages.Count == 0, "Popup binding errors: " + string.Join("\n", bindingErrors.Messages.Take(12)));
            Console.WriteLine($"PASS: {_checks} popup checks");
            return;
        }
        VerifyCore();
        NetworkChecks.Run(Check, args.Contains("--verify-warp-package") ? args[Array.IndexOf(args, "--verify-warp-package") + 1] : null);
        FeatureChecks.Run(Check);
        SystemTuningChecks.Run(Check);
        SettingsChecks.Run(Check);
        ClientFilesChecks.Run(Check);
        VersionChecks.Run(Check);
        AppUpdateChecks.Run(Check);
        AppUpdateFlowChecks.Run(Check);
        AppUpdateHandoffChecks.Run(Check);
        InstallerChecks.Run(Check);
        InstallLifecycleChecks.Run(Check);
        ServerBrowserChecks.Run(Check);
        GameSearchChecks.Run(Check);
        if (args.Contains("--verify-public-network")) ServerBrowserChecks.VerifyPublicNetwork(Check);
        ActivityQueryChecks.Run(Check);
        LoggerChecks.Run(Check);
        WatcherChecks.Run(Check);
        SessionStateFileChecks.Run(Check);
        ReliabilityAuditChecks.Run(Check);
        HistoryChecks.Run(Check);
        VerifyThemes(app);
        PopupChecks.Run(Check, Path.GetDirectoryName(output)!, SaveVisual);
        if (args.Contains("--calibrate"))
        {
            Console.WriteLine("Running isolated installation calibration");
            Task.Run(() => RegionCalibrationService.RunAsync()).GetAwaiter().GetResult();
            Console.WriteLine(File.ReadAllText(Path.Combine(Paths.Cache, "RegionCalibration.json")));
            foreach (string line in App.Logger.History.Where(x => x.Contains("RegionCalibration"))) Console.WriteLine(line);
        }
        App.Settings.Prop.Theme = Bloxstrap.Enums.Theme.CrimsonContract;
        App.Settings.Prop.SelectedBackdrop = Bloxstrap.Enums.WindowsBackdrops.None;
        Console.WriteLine("Creating main window"); var window = new MainWindow(false); Console.WriteLine("Window ready");
        window.Width = 1280;
        window.Height = 760;
        Console.WriteLine("Creating page"); window.RootFrame.Content = new CompetitivePage(); Console.WriteLine("Page ready");
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        root.DataContext = window.DataContext;
        root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
        root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
        window.Content = null;
        root.Width = 1280;
        root.Height = 760;
        root.Measure(new Size(1280, 760));
        root.Arrange(new Rect(0, 0, 1280, 760));
        root.UpdateLayout();
        Pump();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, 0, 1280, 760));
            drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, 1280, 760));
        }
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output)) encoder.Save(file);
        Console.WriteLine(output);
        foreach (var theme in new[] { Theme.Blue, Theme.Light })
        {
            App.Settings.Prop.Theme = theme;
            var themedWindow = new MainWindow(false);
            themedWindow.RootFrame.Content = new CompetitivePage();
            var themedRoot = (FrameworkElement)themedWindow.Content;
            themedRoot.DataContext = themedWindow.DataContext;
            themedRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, themedWindow.Foreground);
            themedWindow.Content = null;
            Check(themedRoot.GetValue(System.Windows.Controls.Panel.BackgroundProperty) is SolidColorBrush, "New window uses solid background: " + theme);
            SaveVisual(themedRoot, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-" + theme + ".png"), 1280, 760);
        }
        foreach (var page in new FrameworkElement[] { new RegionSelectorPage(), new AdvancedRobloxSettingsPage(), new DepthStrapSettingsPage(), new AntiApiPage(), new AppearancePage(), new FastFlagEditorPage() })
        {
            Console.WriteLine("Layout: " + page.GetType().Name);
            page.Measure(new Size(990, 680)); page.Arrange(new Rect(0, 0, 990, 680)); page.UpdateLayout();
            Check(page.ActualWidth > 0, "Settings page creates and lays out: " + page.GetType().Name);
        }
        App.Settings.Prop.Theme = Theme.CrimsonContract;
        BrandTheme.Apply(app.Resources, Theme.CrimsonContract);
        var launchSettingsPage = new AdvancedRobloxSettingsPage();
        CheckReleaseDropdown(launchSettingsPage);
        launchSettingsPage.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, app.Resources["TextFillColorPrimaryBrush"]);
        SaveVisual(new System.Windows.Controls.Border { Background = (Brush)app.Resources["ApplicationBackground"], Padding = new Thickness(20),
            Child = new System.Windows.Controls.Frame { NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden, Content = launchSettingsPage } },
            Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-RobloxBuilds.png"), 1000, 750);
        var appSettingsPage = new DepthStrapSettingsPage();
        appSettingsPage.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, app.Resources["TextFillColorPrimaryBrush"]);
        SaveVisual(new System.Windows.Controls.Border { Background = (Brush)app.Resources["ApplicationBackground"], Padding = new Thickness(20),
            Child = new System.Windows.Controls.Frame { NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden, Content = appSettingsPage } },
            Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-AppSettings.png"), 1000, 750);
        App.FastFlags.SetValue("FFlagDebugSkyGray", "True");
        App.FastFlags.SetValue("FIntDebugForceMSAASamples", "1");
        var editor = new FastFlagEditorPage(); editor.ReloadList();
        editor.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, app.Resources["TextFillColorPrimaryBrush"]);
        SaveVisual(new System.Windows.Controls.Border { Background = (Brush)app.Resources["ApplicationBackground"], Padding = new Thickness(20), Child = new System.Windows.Controls.Frame { NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden, Content = editor } }, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-FastFlags.png"), 1000, 650);
        var setup = new Bloxstrap.UI.Elements.Dialogs.RegionCalibrationDialog(true);
        var setupRoot = (FrameworkElement)setup.Content; setupRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, setup.Foreground);
        setup.Content = null;
        SaveVisual(new System.Windows.Controls.Border { Background = setup.Background, Child = setupRoot }, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-NetworkSetup.png"), 610, 450);
        var tuning = new Bloxstrap.UI.Elements.Dialogs.SystemPerformanceDialog();
        var tuningRoot = (FrameworkElement)tuning.Content;
        tuningRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, tuning.Foreground); tuning.Content = null;
        SaveVisual(new System.Windows.Controls.Border { Background = tuning.Background, Child = tuningRoot }, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-SystemPerformance.png"), 720, 820);
        var launcher = new DepthStrapDialog { Message = "Preparing Roblox…", ProgressMaximum = 100, ProgressValue = 64, CancelEnabled = true };
        var launcherRoot = (FrameworkElement)launcher.Content;
        launcherRoot.DataContext = launcher.DataContext;
        launcherRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, launcher.Foreground);
        launcher.Content = null;
        SaveVisual(launcherRoot, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-Launcher.png"), 720, 420);
        var menu = new Bloxstrap.UI.Elements.Dialogs.LaunchMenuDialog();
        var menuRoot = (FrameworkElement)menu.Content;
        menuRoot.DataContext = menu.DataContext;
        menuRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, menu.Foreground);
        menu.Content = null;
        SaveVisual(menuRoot, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-LaunchMenu.png"), 580, 260);
        var fullIcon = new BitmapImage(new Uri("pack://application:,,,/DepthStrap;component/Resources/Brand/DepthStrap.png"));
        Check(fullIcon.PixelWidth >= 1000, "Launch menu uses the full resolution illustration rather than a small ICO frame");
        var alert = new Bloxstrap.UI.Elements.Dialogs.BadRegionAlertWindow("NON-PREFERRED SERVER", "Toronto, Canada\nNon-preferred Chime server\nTarget: London | Actual: Toronto, Canada");
        var alertRoot = (FrameworkElement)alert.Content; alertRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, alert.Foreground);
        alert.Content = null;
        SaveVisual(new System.Windows.Controls.Border { Background = alert.Background, Child = alertRoot }, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-BadRegion.png"), 390, 220);
        var rejoin = new Bloxstrap.UI.Elements.Dialogs.AutoLogRejoinWindow(() => true);
        var rejoinRoot = (FrameworkElement)rejoin.Content;
        rejoinRoot.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, rejoin.Foreground);
        rejoin.Content = null;
        SaveVisual(new System.Windows.Controls.Border { Background = rejoin.Background, Child = rejoinRoot }, Path.Combine(Path.GetDirectoryName(output)!, "DepthStrap-Rejoin.png"), 390, 210);
        Check(bindingErrors.Messages.Count == 0, "Active page binding errors: " + string.Join("\n", bindingErrors.Messages.Take(12)));
        Console.WriteLine($"PASS: {_checks} checks, seven settings pages, all theme popup previews, network setup, menu, alert and rejoin previews.");
        Environment.Exit(0);
    }

    private static void CheckReleaseDropdown(AdvancedRobloxSettingsPage launchSettingsPage)
    {
        var versionPanel = (FrameworkElement)launchSettingsPage.FindName("VersionSettingsPanel");
        ((Bloxstrap.UI.ViewModels.Settings.RobloxVersionArchiveViewModel)versionPanel.DataContext).ReleaseLabels =
            () => Task.FromResult<(string?, string?)>(("0.742.0.7421053", "0.741.0.7411058"));
        var fixtureWindow = new Window { Width = 1000, Height = 750, ShowActivated = false,
            Content = new System.Windows.Controls.Frame { NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden, Content = launchSettingsPage } };
        fixtureWindow.Show();
        try
        {
        launchSettingsPage.Measure(new Size(990, 680)); launchSettingsPage.Arrange(new Rect(0, 0, 990, 680));
        launchSettingsPage.UpdateLayout();
        static IEnumerable<DependencyObject> Children(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var nested in Children(child)) yield return nested;
            }
        }
        var releaseInput = Children(launchSettingsPage).OfType<System.Windows.Controls.TextBox>()
            .Single(input => Equals(input.Tag, "ReleaseNumberInput"));
        var releasePopup = Children(launchSettingsPage).OfType<System.Windows.Controls.Primitives.Popup>()
            .Single(popup => Equals(popup.Tag, "ReleaseNumberPopup"));
        var releaseOptions = (System.Windows.Controls.ListBox)((System.Windows.Controls.Border)releasePopup.Child).Child;
        var releaseVm = (Bloxstrap.UI.ViewModels.Settings.RobloxVersionArchiveViewModel)releaseInput.DataContext;
        releaseVm.ReleaseCatalog = () => Task.FromResult(new[] { "0.741.0.7411058", "0.741.0.7411059", "0.742.0.7420001" });
        int suggestionInstalls = 0;
        releaseVm.RunInstaller = () => { suggestionInstalls++; return Task.FromResult(0); };
        releaseInput.Text = "741";
        releaseInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        HistoryChecks.Wait(releaseVm.UpdateSuggestionsAsync()); Pump();
        Check(releasePopup.IsOpen && releaseOptions.Items.Count == 2 && releaseVm.VersionId == "741",
            "Editable release dropdown uses actual XAML bindings to show matching Player releases");
        releaseOptions.SelectedIndex = 0; Pump();
        Check(releaseVm.VersionId == "0.741.0.7411059" && releaseInput.Text == releaseVm.VersionId && !releasePopup.IsOpen && suggestionInstalls == 0,
            "Selecting an actual dropdown item fills the full release and never starts installation");
        releaseInput.Text = "742";
        releaseInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        HistoryChecks.Wait(releaseVm.UpdateSuggestionsAsync()); Pump();
        Check(releaseVm.VersionId == "742" && releaseOptions.Items.Count == 1 && releasePopup.IsOpen,
            "Editing a selected release to another family replaces the dropdown without losing typed text");
        releaseVm.CancelLookup();
        }
        finally
        {
            if (fixtureWindow.Content is System.Windows.Controls.Frame fixtureFrame) fixtureFrame.Content = null;
            fixtureWindow.Content = null;
            fixtureWindow.Close();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void SaveVisual(FrameworkElement root, string output, int width, int height)
    {
        root.Width = width; root.Height = height;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Pump(); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(output); encoder.Save(file);
    }

    private static void VerifyThemes(Application app)
    {
        var settings = new Settings { Theme = Theme.Blue, BackgroundImagePath = "old-personal-background.png", CompetitiveFpsCap = 144 };
        BrandTheme.Migrate(settings);
        Check(settings.Theme == Theme.CrimsonContract && settings.BackgroundImagePath is null && settings.CompetitiveFpsCap == 144, "One-time appearance migration preserves gameplay settings");
        settings.Theme = Theme.Green; BrandTheme.Migrate(settings);
        Check(settings.Theme == Theme.Green, "Migration preserves later theme choice");
        foreach (var theme in BrandTheme.Choices)
        {
            BrandTheme.Apply(app.Resources, theme);
            Check(theme == Theme.CrimsonContract ? app.Resources["ApplicationBackground"] is ImageBrush : app.Resources["ApplicationBackground"] is SolidColorBrush, "Artwork only belongs to Crimson Contract: " + theme);
        }
        BrandTheme.Apply(app.Resources, Theme.CrimsonContract);
        var artwork = ((ImageBrush)app.Resources["ApplicationBackground"]).ImageSource as BitmapSource;
        Check(artwork?.PixelWidth > 1000, "Packaged background loads");
        BrandTheme.Apply(app.Resources, Theme.Light);
        Check(app.Resources["ApplicationBackground"] is SolidColorBrush, "Switching to a solid theme clears artwork");
        using var icon = File.OpenRead("Bloxstrap/DepthStrap.ico");
        var decoder = new IconBitmapDecoder(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(decoder.Frames.Count == 7 && decoder.Frames.Max(f => f.PixelWidth) == 256, "Windows icon contains seven resolutions");
        var largest = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        var pixels = new byte[largest.PixelHeight * largest.PixelWidth * 4];
        new FormatConvertedBitmap(largest, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, largest.PixelWidth * 4, 0);
        Check(pixels[3] == 0 && pixels[((128 * 256) + 128) * 4 + 3] == 0, "Icon background and central cutout remain transparent");
    }

    private static void VerifyCore()
    {
        VerifyConcurrentBackup();
        App.Settings.Prop = new Settings();
        Directory.CreateDirectory(Paths.Cache);
        File.WriteAllText(Path.Combine(Paths.Cache, "RegionCalibration.json"), "{\"status\":\"Saved calibration fixture\"}");
        Check(AdaptiveRegionService.Status == "Saved calibration fixture", "Calibration status survives a new process");
        Check(App.Settings.Prop.CompetitivePreferredCity.Length == 0 && App.Settings.Prop.CompetitiveFallbackCities.Count == 0, "No personal region defaults");
        Check(App.Settings.Prop.MatchFpsToMonitorRefreshRate && App.Settings.Prop.CompetitiveGraphicsQualityLevel == 3, "Requested competitive defaults");
        var now = DateTimeOffset.UtcNow;
        var observations = Enumerable.Range(0, 5).Select(i => new RegionObservation { City = "Region A", Country = "US", Route = "direct", LocalHour = 8, Timestamp = now.AddMinutes(-i), AverageMs = 40 })
            .Concat(Enumerable.Range(0, 3).Select(i => new RegionObservation { City = "Region B", Country = "US", Route = "direct", LocalHour = 8, Timestamp = now.AddMinutes(-i), AverageMs = i == 2 ? 900 : 20 }))
            .Concat(new[] { new RegionObservation { City = "Tunnel only", Country = "US", Route = "WARP:ABC", IsCalibration = true, Timestamp = now, AverageMs = 1 }, new RegionObservation { City = "Invalid", Route = "direct", Timestamp = now, AverageMs = double.NaN } }).ToList();
        var ranked = AdaptiveRegionPlanner.Rank(observations, "direct", 8, now);
        Check(ranked.Count == 2 && ranked[0].City == "Region B" && ranked[0].CostMs == 20, "Ranking uses medians and isolates routes");
        Check(AdaptiveRegionPlanner.Rank(observations, "direct", 20, now).Single().City == "Region A", "Sparse time bucket falls back to sufficient overall evidence");
        Check(AdaptiveRegionPlanner.Stabilize(new[] { new LearnedRegion("New", 96, 5, ""), new LearnedRegion("Current", 100, 5, "") }, "Current")[0].City == "Current", "Region hysteresis prevents oscillation");
        Check(AdaptiveRegionPlanner.Stabilize(new[] { new LearnedRegion("New", 80, 5, ""), new LearnedRegion("Current", 100, 5, "") }, "Current")[0].City == "New", "Material region improvement changes preference");
        var uri = RobloxLaunchUri.TryParse("placeId=123&accessCode=a%2Bb%20c&launchData=x%26y&other=kept")!;
        uri.SetParameter("gameInstanceId", "test-job");
        var rebuilt = RobloxLaunchUri.TryParse(uri.Build())!;
        Check(rebuilt.AccessCode == "a+b c" && rebuilt.LaunchData == "x&y" && rebuilt.GetParameter("other") == "kept" && rebuilt.HasExplicitServerId, "Launch URI preserves private codes and unrelated data");
        Check(RobloxLaunchUri.TryParse("roblox://navigation/home") is null, "Non-experience URIs remain untouched");
        Check(RobloxVersionArchive.IsVersionId("version-0123456789abcdef") && !RobloxVersionArchive.IsVersionId("version-0123456789abcdef\n") && !RobloxVersionArchive.IsVersionId("../version-0123456789abcdef"), "Version IDs reject newline and path injection");
        Check(new CloudflareTraceResult { Warp = "unknown" }.WarpActive is null && new CloudflareTraceResult { Warp = "plus" }.WarpActive is true && new CloudflareTraceResult { Warp = "off" }.WarpActive is false, "Unknown WARP state remains unknown");
        var powerState = typeof(CompetitivePerformanceManager).GetNestedType("PROCESS_POWER_THROTTLING_STATE", BindingFlags.NonPublic)!;
        Check(System.Runtime.InteropServices.Marshal.SizeOf(powerState) == 12, "Power API structure matches Windows");
        using (var self = Process.GetCurrentProcess())
        {
            var priority = self.PriorityClass; CompetitivePerformanceManager.ApplyToProcess(self.Id);
            Check(self.PriorityClass == priority, "Player-only performance guard leaves unrelated processes untouched");
        }
        Directory.CreateDirectory(Paths.Roblox);
        App.GlobalSettings.Document = XDocument.Parse("<roblox version='4'><Item class='UserGameSettings'><Properties><int name='FramerateCap'>60</int><token name='SavedQualityLevel'>7</token></Properties></Item></roblox>");
        App.GlobalSettings.Loaded = true; App.GlobalSettings.Save();
        App.Settings.Prop.MatchFpsToMonitorRefreshRate = false; App.Settings.Prop.CompetitiveFpsCap = 144;
        App.FastFlags.Prop.Clear();
        CompetitivePerformanceManager.ApplyPreLaunchSettings();
        Check(App.GlobalSettings.GetPreset("Rendering.FramerateCap") == "144" && App.GlobalSettings.GetPreset("Rendering.SavedQualityLevel") == "3" && App.GlobalSettings.GetReadOnly(), "Competitive applies real FPS, quality 3 and quality lock");
        Check(App.FastFlags.GetPreset("Rendering.MSAA1") == "1" && App.FastFlags.GetPreset("Rendering.TextureQuality.Level") == "0" && App.FastFlags.GetPreset("Rendering.LowPolyMeshes1") == "0" && App.FastFlags.GetPreset("Rendering.RemoveGrass2") == "0" && App.FastFlags.GetPreset("Rendering.FrmQuality") is null, "Competitive uses MSAA 1x, texture 0, minimum mesh/grass distance and preserves quality 3");
        CompetitiveSettingsBackup.ReleaseQualityLock();
        App.GlobalSettings.SetPreset("Rendering.FramerateCap", "165"); App.GlobalSettings.Save();
        App.Settings.Prop.CompetitiveModeEnabled = false; CompetitivePerformanceManager.ApplyPreLaunchSettings();
        Check(App.GlobalSettings.GetPreset("Rendering.FramerateCap") == "165" && App.GlobalSettings.GetPreset("Rendering.SavedQualityLevel") == "7" && !App.GlobalSettings.GetReadOnly(), "Disabling restores owned values and preserves manual edits");
        Check(App.FastFlags.Prop.Count == 0, "Disabling restores original flags");
        App.Settings.Prop.CompetitiveModeEnabled = true;
        Task.WhenAll(Enumerable.Range(0, 64).Select(i => CompetitiveSessionLogger.WriteNetworkEventAsync(new CompetitiveNetworkEvent { JobId = "test-" + i, Timestamp = DateTime.Now, Cloudflare = new CloudflareTraceResult { Warp = "unknown", PublicIp = "203.0.113.5" } }))).GetAwaiter().GetResult();
        var lines = File.ReadAllLines(CompetitiveSessionLogger.GetJsonlPath());
        Check(lines.Length == 64 && lines.All(line => { using var json = JsonDocument.Parse(line); return !line.Contains("203.0.113.5"); }), "Concurrent session writes produce intact lines and mask egress IP");
        var workers = Enumerable.Range(0, 2).Select(i =>
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--log-worker"); start.ArgumentList.Add(Paths.Base); start.ArgumentList.Add("worker" + i);
            return Process.Start(start)!;
        }).ToList();
        foreach (var worker in workers) { Check(worker.WaitForExit(15000) && worker.ExitCode == 0, "Concurrent worker exits successfully"); worker.Dispose(); }
        Check(File.ReadAllLines(CompetitiveSessionLogger.GetJsonlPath()).Length == 144, "Multiple processes retain all session log lines");
        Check(JsonSerializer.Deserialize<List<RegionObservation>>(File.ReadAllText(Path.Combine(Paths.Cache, "LearnedRegions.json")))!.Count == 80, "Multiple processes retain all learned observations");
        foreach (var version in new[] { "version-0123456789abcdef", "version-fedcba9876543210" })
        { var directory = Path.Combine(Paths.Versions, version); Directory.CreateDirectory(directory); ClientFilesChecks.WriteExecutable(Path.Combine(directory, App.RobloxPlayerAppName)); }
        App.Settings.Prop.RobloxVersionArchiveLimit = 1; App.Settings.Prop.RobloxPlayerVersionOverride = "version-0123456789abcdef";
        Check(RobloxVersionArchive.InstalledPlayerVersions().Count == 2 && RobloxVersionArchive.RetainedVersions().Contains("version-0123456789abcdef"), "Archive retains pinned build");
        App.Settings.Prop = new Settings();
        var presetModel = new Bloxstrap.UI.ViewModels.Settings.CompetitivePageViewModel();
        presetModel.ApplyBalancedPresetCommand.Execute(null);
        Check(!App.Settings.Prop.CompetitiveAggressiveRendering && App.Settings.Prop.MatchFpsToMonitorRefreshRate && App.Settings.Prop.AdaptiveRegionPreferencesEnabled && App.Settings.Prop.AutoSelectPreferredServerOnLaunch, "Balanced retains monitor FPS and adaptive networking");
        presetModel.ApplyMaximumPresetCommand.Execute(null);
        Check(App.Settings.Prop.CompetitiveAggressiveRendering && App.Settings.Prop.CompetitiveGraphicsQualityLevel == 3 && App.Settings.Prop.MatchFpsToMonitorRefreshRate, "Competitive preset restores quality 3 and monitor FPS");
        Console.WriteLine("Detected monitor maximum: " + (MonitorRefreshRateService.DetectMaximumHz()?.ToString() ?? "unavailable") + " Hz");
    }

    private static void VerifyConcurrentBackup()
    {
        string root = Path.Combine(Paths.Base, "backup-workers");
        string target = DateTime.UtcNow.AddSeconds(2).Ticks.ToString();
        var keys = new[] { "Rendering.MSAA1", "Rendering.RemoveGrass1", "Rendering.RemoveGrass2", "Rendering.LowPolyMeshes1" };
        var workers = keys.Select(key =>
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (string argument in new[] { "--backup-worker", root, key, target }) start.ArgumentList.Add(argument);
            return Process.Start(start)!;
        }).ToList();
        foreach (var worker in workers)
        {
            Check(worker.WaitForExit(15000) && worker.ExitCode == 0, "Concurrent settings-backup worker completes");
            worker.Dispose();
        }
        string cache = Path.Combine(root, "Cache");
        using var backup = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, "CompetitiveSettingsBackup.json")));
        var flags = backup.RootElement.GetProperty("Flags");
        Check(flags.EnumerateObject().Count() == keys.Length && keys.All(key =>
            flags.GetProperty(key).GetProperty("Applied").GetString() == "19" &&
            flags.GetProperty(key).GetProperty("Original").ValueKind == JsonValueKind.Null),
            "Concurrent backup writes preserve every original value and final applied value");
        Check(!Directory.EnumerateFiles(cache, "*.tmp").Any(), "Settings backup leaves no temporary files");
    }
}
