using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.Extensions;
using Bloxstrap.UI;
using Bloxstrap.UI.Elements.Settings;
using Bloxstrap.UI.Elements.Settings.Pages;
using SavedWindowState = Bloxstrap.Models.Persistable.WindowState;

internal static class ShellLayoutChecks
{
    internal static void Run(Action<bool, string> check, string outputDirectory,
        Action<FrameworkElement, string, int, int> saveVisual)
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var leftMonitor = new Rect(-1920, -200, 1920, 1040);
        var monitors = new[] { workArea, leftMonitor };
        var bounds = MainWindow.FitWindowBounds(new SavedWindowState
            { Width = 250, Height = 250, Left = -1800, Top = -100 }, monitors);
        check(bounds == new Rect(-1800, -100, 960, 600), "Small saved windows recover usable dimensions and retain negative monitor coordinates");
        bounds = MainWindow.FitWindowBounds(new SavedWindowState
            { Width = 5000, Height = 4000, Left = 9000, Top = 9000 }, monitors);
        check(bounds == workArea, "Oversized and off-screen saved windows fit the nearest monitor work area");
        bounds = MainWindow.FitWindowBounds(new SavedWindowState
            { Width = double.NaN, Height = double.PositiveInfinity, Left = double.NaN }, monitors);
        check(bounds == new Rect(320, 120, 1280, 800), "Invalid saved geometry restores centered finite defaults");
        var smallScreen = new Rect(0, 0, 800, 540);
        bounds = MainWindow.FitWindowBounds(new SavedWindowState(), new[] { smallScreen });
        check(bounds == smallScreen, "A small work area limits minimum window dimensions");
        var smallSecondary = new Rect(-1280, 1100, 1280, 680);
        bounds = MainWindow.FitWindowBounds(new SavedWindowState
            { Width = 1500, Height = 900, Left = -1200, Top = 1200 }, new[] { workArea, smallSecondary });
        check(bounds == smallSecondary, "Saved windows fit a smaller secondary monitor instead of the primary monitor size");
        bounds = MainWindow.FitWindowBounds(new SavedWindowState
            { Width = 960, Height = 600, Left = -1800, Top = 0 }, new[] { workArea, smallSecondary });
        check(workArea.Contains(bounds) || smallSecondary.Contains(bounds), "Saved windows in staggered monitor gaps move entirely onto a real monitor");

        var originalTheme = App.Settings.Prop.Theme;
        bool originalExpanded = App.Settings.Prop.IsNavigationSidebarExpanded;
        var originalPage = App.State.Prop.LastPage;
        try
        {
            App.State.Prop.LastPage = null;
            foreach (var theme in new[] { Theme.CrimsonContract, Theme.Light })
            foreach (bool expanded in new[] { true, false })
            {
                App.Settings.Prop.Theme = theme;
                App.Settings.Prop.IsNavigationSidebarExpanded = expanded;
                var window = new MainWindow(false);
                window.LaunchMenu.PlacementTarget = window.LaunchButton;
                window.RootFrame.Content = new CompetitivePage();
                // The detached preview supplies its page; native navigation requires a loaded window.
                ((Wpf.Ui.Controls.NavigationItem)window.RootNavigation.Items[0]).IsActive = true;
                var root = (FrameworkElement)window.Content;
                root.DataContext = window.DataContext;
                root.SetValue(TextElement.ForegroundProperty, window.Foreground);
                root.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
                window.Content = null;
                string file = Path.Combine(outputDirectory, $"DepthStrap-Shell-{theme}-{(expanded ? "Expanded" : "Compact")}.png");
                saveVisual(root, file, 960, 600);
                check(window.RootNavigation.IsExpanded == expanded && (expanded ? window.RootNavigation.ActualWidth >= 200 : window.RootNavigation.ActualWidth <= 80),
                    $"Requested sidebar state {expanded} is rendered; actual state {window.RootNavigation.IsExpanded}, width {window.RootNavigation.ActualWidth}");
                foreach (var control in new FrameworkElement[] { window.LaunchButton, window.SaveButton,
                    window.RestartButton, window.CloseButton, window.ContentSurface, window.NavigationSurface, window.HeroSurface })
                {
                    var position = control.TranslatePoint(new Point(), root);
                    check(control.ActualWidth > 0 && control.ActualHeight > 0 && position.X >= 0 && position.Y >= 0 &&
                        position.X + control.ActualWidth <= root.ActualWidth + 1 && position.Y + control.ActualHeight <= root.ActualHeight + 1,
                        $"{theme} {(expanded ? "expanded" : "compact")} shell keeps {control.Name} within minimum window bounds");
                }
                check(window.RootNavigation.Items.Count == 7, "Rebuilt shell retains all seven settings destinations");
                check(window.LaunchMenu.Items.OfType<MenuItem>().All(item => item.Command != null), "Both Player and Studio launch menu commands remain bound");
                check(window.SaveButton.Command != null && window.CloseButton.Command != null && window.RestartButton.Command != null,
                    "Secondary footer actions retain their commands");
                if (theme == Theme.CrimsonContract && expanded)
                {
                    saveVisual(root, Path.Combine(outputDirectory, "DepthStrap-Crimson-Review.png"), 1280, 800);
                    saveVisual(root, Path.Combine(outputDirectory, "DepthStrap-SmallScreen.png"), 800, 540);
                    var launch = window.LaunchButton.TranslatePoint(new Point(), root);
                    check(launch.X >= 0 && launch.X + window.LaunchButton.ActualWidth <= 800 &&
                        launch.Y + window.LaunchButton.ActualHeight <= 540 && window.RootFrame.ActualWidth >= 400 && window.RootFrame.ActualHeight >= 150,
                        "Small work area retains the primary action and a usable scrollable page");
                }
            }
        }
        finally
        {
            App.Settings.Prop.Theme = originalTheme;
            App.Settings.Prop.IsNavigationSidebarExpanded = originalExpanded;
            App.State.Prop.LastPage = originalPage;
            BrandTheme.Apply(Application.Current.Resources, originalTheme.GetFinal());
        }
    }
}
