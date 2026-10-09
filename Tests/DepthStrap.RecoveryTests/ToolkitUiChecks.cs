using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Bloxstrap;
using Bloxstrap.UI.Elements.Settings.Pages;
using Bloxstrap.Roblox;
using Bloxstrap.UI;
using Wpf.Ui.Controls;
using SettingsWindow = Bloxstrap.UI.Elements.Settings.MainWindow;

internal static class ToolkitUiChecks
{
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Settle()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [STAThread]
    internal static void Run(string reportFolder)
    {
        string reports = Path.GetFullPath(reportFolder); Directory.CreateDirectory(reports);
        Paths.Initialize(Path.Combine(reports, "fixture-" + Guid.NewGuid().ToString("N")));
        typeof(Paths).GetProperty("Roblox")!.SetValue(null, Path.Combine(Paths.Base, "Roblox"));
        CompetitiveSettingsBackup.PlayerPresence = () => false;
        typeof(App).GetProperty("LaunchSettings")!.SetValue(null, new LaunchSettings(new[] { "-testmode" }));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = XDocument.Load("Bloxstrap/App.xaml");
        var element = document.Root!.Elements().First().Elements().First();
        foreach (var converter in element.Elements().Where(x => x.Name.NamespaceName.StartsWith("clr-namespace:")).ToList()) converter.Remove();
        foreach (var attribute in document.Root.Attributes().Where(a => a.IsNamespaceDeclaration))
            if (element.Attribute(attribute.Name) is null) element.SetAttributeValue(attribute.Name, attribute.Value.Replace("clr-namespace:Bloxstrap.UI.Converters", "clr-namespace:Bloxstrap.UI.Converters;assembly=DepthStrap"));
        string resourceXml = element.ToString().Replace("clr-namespace:Bloxstrap.UI.Converters\"", "clr-namespace:Bloxstrap.UI.Converters;assembly=DepthStrap\"").Replace("Source=\"UI/Style/", "Source=\"pack://application:,,,/DepthStrap;component/UI/Style/");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resourceXml);
        foreach (string name in new[] { "StringFormatConverter", "RangeConverter", "EnumNameConverter", "BooleanToVisibilityConverter", "InverseBooleanToVisibilityConverter", "NumberAbbreviationConverter" })
            app.Resources[name] = Activator.CreateInstance(typeof(App).Assembly.GetType("Bloxstrap.UI.Converters." + name)!, true)!;
        BrandTheme.Apply(app.Resources, Bloxstrap.Enums.Theme.CrimsonContract);
        string appearanceRoot = Path.Combine(Paths.Base, "helper-appearance-fixture"); Directory.CreateDirectory(appearanceRoot);
        string appearanceFile = Path.Combine(appearanceRoot, "Settings.json");
        File.WriteAllText(appearanceFile, "{\"Theme\":\"Blue\"}");
        DepthStrap.Recovery.RecoveryTheme.InitializeHelperAppearance(Path.Combine(appearanceRoot, "DepthStrap.exe"));
        Check(App.Settings.Prop.Theme == Bloxstrap.Enums.Theme.Blue && app.Resources["PopupBackground"] is SolidColorBrush,
            "Elevated helper loads the installed app's selected background without normal startup");
        File.WriteAllText(appearanceFile, "{broken");
        DepthStrap.Recovery.RecoveryTheme.InitializeHelperAppearance(Path.Combine(appearanceRoot, "DepthStrap.exe"));
        Check(App.Settings.Prop.Theme == Bloxstrap.Enums.Theme.CrimsonContract && app.Resources["PopupBackground"] is DrawingBrush,
            "Unreadable helper appearance falls back safely without repairing or overwriting settings");
        Check(File.ReadAllText(appearanceFile) == "{broken", "Helper appearance loading is read-only");
        var window = new SettingsWindow(false);
        window.Show(); Pump();
        var navigation = window.GetNavigation();
        Check(navigation.Items.OfType<NavigationItem>().Count(item => Equals(item.Tag, "antiapi")) == 1 &&
            navigation.Items.OfType<NavigationItem>().Single(item => Equals(item.Tag, "antiapi")).Content?.ToString() == "Anti Api ( Beta )",
            "Loaded settings must attach one toolkit entry with the requested Anti Api ( Beta ) label");
        var menu = navigation.Items.OfType<NavigationItem>().ToList();
        Check(menu.FindIndex(item => Equals(item.Tag, "antiapi")) + 1 == menu.FindIndex(item => item.PageType == typeof(Bloxstrap.UI.Elements.Settings.Pages.AppearancePage)), "Anti API must be directly above Appearance");
        int count = navigation.Items.Count;
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));
        Check(navigation.Items.Count == count, "Repeated load must not duplicate the toolkit entry");
        Check(!window.Title.Contains("local toolkit test build"), "The public build must use the normal app title");
        string? normal = App.State.Prop.LastPage;
        Check(window.Navigate(typeof(AntiApiPage)), "The toolkit settings page must be navigable"); Pump();
        Check(window.GetFrame().Content is AntiApiPage, "Navigation must load the custom toolkit page");
        Settle();
        var page = (AntiApiPage)window.GetFrame().Content;
        Check(page.IsVisible && page.ActualHeight > 0 && page.ActualWidth > 0, "The loaded toolkit page must participate in visible layout");
        Check(App.State.Prop.LastPage == typeof(AntiApiPage).FullName, "Production navigation persists its supported toolkit page");
        Render(window, Path.Combine(reports, "toolkit-settings.png"));
        Check(page.Foreground is SolidColorBrush text && text.Color.R > 200, "Arena toolkit text must use the readable light theme foreground");
        var originalFont = window.FontFamily;
        window.FontFamily = new FontFamily("Consolas");
        string resetFolder = Path.Combine(Paths.Base, "reset-dialog-fixture"); Directory.CreateDirectory(resetFolder);
        string resetCookie = Path.Combine(resetFolder, "RobloxCookies.dat"); File.WriteAllText(resetCookie, "synthetic-preview-only");
        var lab = typeof(App).Assembly;
        object Create(string name, params object[] values) => Activator.CreateInstance(lab.GetType("DepthStrap.Recovery." + name)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, values, null)!;
        var dataService = Create("RobloxDataReset", new[] { resetFolder }, (Func<bool>)(() => false));
        var registryService = lab.GetType("DepthStrap.Recovery.RobloxRegistryReset")!.GetMethod("ForFixture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { Guid.NewGuid(), (Func<bool>)(() => false) })!;
        var dataPreview = Create("ResetPreview", Guid.NewGuid(), "synthetic", new[] { resetFolder }, 1, 22L);
        var registryPreview = Create("RegistryResetPreview", Guid.NewGuid(), "synthetic", 0, "HKCU\\SyntheticPreviewOnly");
        var fullPreview = Create("FullResetPreview", dataPreview, registryPreview);
        Window ResetDialog() => (Window)Create("RobloxDataResetDialog", window, dataService, registryService, fullPreview);
        foreach (var theme in BrandTheme.Choices)
        {
            App.Settings.Prop.Theme = theme;
            BrandTheme.Apply(app.Resources, theme); Pump();
            Check(Equals(page.Foreground, app.Resources["TextFillColorPrimaryBrush"]), "Toolkit page text must track the selected theme");
            Render(window, Path.Combine(reports, "toolkit-settings-" + theme + ".png"));
            var promptType = typeof(App).Assembly.GetType("DepthStrap.Recovery.RecoveryPrompt")!;
            var prompt = (Window)Activator.CreateInstance(promptType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { "Synthetic preview only. No operation runs.", "Anti API theme check", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No }, null)!;
            prompt.Owner = window; prompt.Show(); Pump();
            Check(Equals(prompt.Background, app.Resources["PopupBackground"]) && Equals(prompt.Foreground, app.Resources["TextFillColorPrimaryBrush"]),
                "Anti API child prompts must honor the app's generic-color theme");
            Check(prompt.FontFamily.Equals(window.FontFamily), "Anti API child prompts must inherit the app font");
            Render(prompt, Path.Combine(reports, "toolkit-prompt-" + theme + ".png")); prompt.Close();
            Check((MessageBoxResult)promptType.GetProperty("Result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prompt)! == MessageBoxResult.No,
                "Closing an Anti API destructive confirmation must retain its No result");
            var themeFactory = typeof(App).Assembly.GetType("DepthStrap.Recovery.ToolkitWindowFactory")!;
            var themedToolkit = (Window)themeFactory.GetMethod("Create")!.Invoke(null, null)!;
            themedToolkit.Owner = window; BrandTheme.ApplyPopup(themedToolkit); themedToolkit.Show(); Pump();
            var toolkitSurface = ((Grid)themedToolkit.Content).Children.OfType<Border>().Single();
            Check((theme == Bloxstrap.Enums.Theme.CrimsonContract ? toolkitSurface.Background is DrawingBrush : toolkitSurface.Background is SolidColorBrush) &&
                Equals(toolkitSurface.Background, app.Resources["PopupBackground"]), "Anti API's content surface must match the selected theme");
            Render(themedToolkit, Path.Combine(reports, "toolkit-window-" + theme + ".png")); themedToolkit.Close();
            var reset = ResetDialog(); reset.Show(); Pump();
            var resetPanel = (StackPanel)((ScrollViewer)reset.Content).Content;
            var boxes = resetPanel.Children.OfType<System.Windows.Controls.TextBox>().ToArray();
            var confirm = boxes.Single(box => !box.IsReadOnly);
            var delete = resetPanel.Children.OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "Permanently delete listed Roblox data"));
            Check(!delete.IsEnabled, "Full reset must begin without deletion enabled");
            Check(boxes.Single(box => box.IsReadOnly).Text.Contains(resetFolder) && boxes.Single(box => box.IsReadOnly).Text.Contains("HKCU\\SyntheticPreviewOnly"), "Full reset must display both previewed stores");
            Check(Equals(reset.Background, app.Resources["PopupBackground"]) && reset.FontFamily.Equals(window.FontFamily), "Full reset dialog must honor generic themes and owner font");
            delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(reset.DialogResult != true, "Even a directly raised delete action must reject absent confirmation");
            confirm.Text = "reset"; Check(!delete.IsEnabled, "Lowercase confirmation must not enable deletion");
            confirm.Text = "RESET"; Check(delete.IsEnabled, "Exact RESET must enable the explicit deletion action");
            confirm.Text = ""; Check(!delete.IsEnabled, "Clearing confirmation must disable deletion again");
            Render(reset, Path.Combine(reports, "toolkit-full-reset-" + theme + ".png")); reset.Close();
            Check(reset.DialogResult != true, "Closing a full reset preview must not approve deletion");
            string adapters = System.Text.Json.JsonSerializer.Serialize(new[] {
                new { Id = Guid.NewGuid(), Name = "Synthetic supported adapter", Supported = true, Backend = "fixture", Reason = "", Physical = true },
                new { Id = Guid.NewGuid(), Name = "Synthetic inspection-only adapter", Supported = false, Backend = "", Reason = "No fixture driver support.", Physical = false }
            });
            var mac = (Window)lab.GetType("DepthStrap.Recovery.MacControls")!.GetMethod("CreateDialog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { window, adapters })!;
            mac.Show(); Pump();
            var macPanel = (StackPanel)((ScrollViewer)mac.Content).Content;
            var adapterPicker = macPanel.Children.OfType<System.Windows.Controls.ComboBox>().Single();
            var macAddress = macPanel.Children.OfType<System.Windows.Controls.TextBox>().Single();
            var macConsent = macPanel.Children.OfType<System.Windows.Controls.CheckBox>().Single();
            var macStatus = macPanel.Children.OfType<TextBlock>().Last();
            var macActions = macPanel.Children.OfType<StackPanel>().Single();
            var macButtons = macActions.Children.OfType<System.Windows.Controls.Button>().ToArray();
            void ClickMac(int index) { macButtons[index].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Pump(); }
            Check(Equals(mac.Background, app.Resources["PopupBackground"]) && mac.FontFamily.Equals(window.FontFamily), "MAC dialog must honor the theme and owner font");
            Check(macAddress.Text.Length == 12 && (Convert.ToByte(macAddress.Text[..2], 16) & 3) == 2, "The dialog must generate a complete local unicast address");
            ClickMac(0); Check(macStatus.Text == "Select the adapter first.", "Apply cannot proceed without selecting an adapter");
            ClickMac(1); Check(macStatus.Text == "Select the adapter first.", "Restore cannot proceed without selecting an adapter");
            adapterPicker.SelectedIndex = 0;
            ClickMac(0); Check(macStatus.Text.Contains("Confirm"), "Apply requires connection-interruption consent");
            ClickMac(1); Check(macStatus.Text.Contains("Confirm"), "Restore requires connection-interruption consent");
            macConsent.IsChecked = true; macAddress.Text = "invalid";
            ClickMac(0); Check(mac.IsEnabled && macStatus.Text == "Enter a complete six-byte MAC address.", "A real invalid-input click must remain usable and show its correction");
            macAddress.Text = "001122334455";
            ClickMac(0); Check(macStatus.Text == "Use a locally administered unicast MAC.", "A global MAC is refused before client checks or elevation");
            adapterPicker.SelectedIndex = 1;
            ClickMac(0); Check(macStatus.Text.Contains("No MAC change was attempted"), "Inspection-only selection cannot apply a MAC");
            ClickMac(1); Check(macStatus.Text.Contains("backup is retained"), "Unavailable restoration must explain backup retention");
            adapterPicker.IsDropDownOpen = true; Pump();
            var adapterPopup = (System.Windows.Controls.Primitives.Popup)adapterPicker.Template.FindName("PART_Popup", adapterPicker);
            Check(adapterPopup.IsOpen && adapterPopup.Child is Border popupSurface && Equals(popupSurface.Background, app.Resources["ControlFillColorDefaultBrush"]), "The open adapter list must use the selected theme's control surface");
            var adapterItem = (ComboBoxItem)adapterPicker.ItemContainerGenerator.ContainerFromIndex(0);
            Check(Equals(adapterItem.Foreground, app.Resources["TextFillColorPrimaryBrush"]), "Adapter list items must have readable themed foregrounds");
            var adapterToggle = (System.Windows.Controls.Primitives.ToggleButton)adapterPicker.Template.FindName("PART_Toggle", adapterPicker);
            Check(adapterToggle.ActualWidth >= adapterPicker.ActualWidth - 1 && adapterToggle.ActualHeight >= 28, "The adapter dropdown's clickable surface must span the whole field");
            adapterPicker.IsDropDownOpen = false; Pump();
            Render(mac, Path.Combine(reports, "toolkit-mac-" + theme + ".png")); mac.Close();
        }
        var approvedPreview = ResetDialog();
        var approveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        approveTimer.Tick += (_, _) =>
        {
            approveTimer.Stop();
            var panel = (StackPanel)((ScrollViewer)approvedPreview.Content).Content;
            panel.Children.OfType<System.Windows.Controls.TextBox>().Single(box => !box.IsReadOnly).Text = "RESET";
            panel.Children.OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "Permanently delete listed Roblox data")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        };
        approveTimer.Start(); Check(approvedPreview.ShowDialog() == true, "Explicit RESET and delete must approve only the dialog result"); approveTimer.Stop();
        Check(File.ReadAllText(resetCookie) == "synthetic-preview-only", "Confirmation dialogs must never execute deletion themselves");
        App.Settings.Prop.Theme = Bloxstrap.Enums.Theme.CrimsonContract;
        BrandTheme.Apply(app.Resources, Bloxstrap.Enums.Theme.CrimsonContract);
        window.FontFamily = originalFont;
        var factory = typeof(App).Assembly.GetType("DepthStrap.Recovery.ToolkitWindowFactory")!;
        var toolkit = (Window)factory.GetMethod("Create")!.Invoke(null, null)!;
        toolkit.Owner = window; BrandTheme.ApplyPopup(toolkit); toolkit.Show(); Pump();
        Check(toolkit.IsVisible && ReferenceEquals(toolkit.Owner, window), "Recovery window must open with the settings owner");
        Check(toolkit.Content is not null, "The recovery UI must have its controls");
        Render(toolkit, Path.Combine(reports, "toolkit-window.png")); toolkit.Close();
        var card = (Border)((ScrollViewer)page.Content).Content;
        var open = ((StackPanel)card.Child).Children.OfType<System.Windows.Controls.Button>().Single();
        bool openedFromSettings = false;
        bool installationPassedFromSettings = false;
        var closeDialog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        closeDialog.Tick += (_, _) =>
        {
            closeDialog.Stop();
            var dialog = app.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.GetType().FullName == "DepthStrap.Recovery.RecoveryWindow");
            openedFromSettings = dialog is not null && ReferenceEquals(dialog.Owner, window);
            installationPassedFromSettings = dialog?.GetType().GetProperty("ManagedInstallationExecutable", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) as string == Paths.Application;
            dialog?.Close();
        };
        closeDialog.Start(); open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); closeDialog.Stop();
        Check(openedFromSettings, "The actual settings button must open an owned modal recovery window");
        Check(installationPassedFromSettings, "The actual settings button must pass the current managed installation to reset previews");
        Check(factory.Assembly == typeof(App).Assembly, "Recovery runtime and helper ship in the DepthStrap assembly");
        Check(!Directory.Exists(Path.Combine(Paths.Base, "Roblox")), "Opening toolkit menus must not install or alter Roblox data");
        window.Hide();
        Console.WriteLine($"PASS: {_checks} production toolkit UI and helper checks; no reset or adapter change executed.");
        app.Shutdown();
    }
    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
