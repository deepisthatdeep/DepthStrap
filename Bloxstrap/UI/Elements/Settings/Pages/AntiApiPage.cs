using System.Windows;
using System.Windows.Controls;
using DepthStrap.Recovery;

namespace Bloxstrap.UI.Elements.Settings.Pages;

public sealed class AntiApiPage : Page
{
    public AntiApiPage()
    {
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Anti Api ( Beta )", FontSize = 24, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Reset Roblox's local data and sign-in files, inspect your installation, or change and restore a supported adapter's MAC address. Review the exact scope before confirming. Windows requests administrator approval for MAC changes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Beta: adapter support depends on its driver. A reset removes local Roblox settings and signs you out. These tools do not guarantee compatibility with third-party software.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var open = new Button { Content = "Open Anti Api ( Beta )", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8, 16, 8) };
        open.Click += (_, _) =>
        {
            var toolkit = ToolkitWindowFactory.CreateForInstallation(Paths.Application);
            toolkit.Owner = Window.GetWindow(this);
            toolkit.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            BrandTheme.ApplyPopup(toolkit);
            toolkit.ShowDialog();
        };
        panel.Children.Add(open);
        var card = new Border { Child = panel, Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(6) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        Content = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
