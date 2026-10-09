using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DepthStrap.Recovery;

/// <summary>Collects confirmation only. Deletion is executed by the caller after this dialog closes.</summary>
internal sealed class RobloxDataResetDialog : Window
{
    internal RobloxDataReset Service { get; private set; }
    internal FullResetPreview Preview { get; private set; }
    private bool _loading;
    internal RobloxDataResetDialog(Window owner, RobloxDataReset service, RobloxRegistryReset registry, FullResetPreview preview)
    {
        Owner = owner; Service = service; Preview = preview;
        Title = "Delete Roblox data folders"; Width = 700; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RecoveryTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Permanently delete the listed Roblox folders, including cookies, settings, logs, caches, Studio recovery files, Store user data and DepthStrap-managed Roblox builds/downloads. The Roblox current-user vendor registry key is also deleted. DepthStrap itself and its settings are preserved.", TextWrapping = TextWrapping.Wrap });
        var locations = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 190, Margin = new Thickness(0, 14, 0, 14), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(locations);
        void Describe() => locations.Text = string.Join("\n", Preview.Data.Locations) + "\n" + Preview.Registry.Location
            + $"\n\n{Preview.Data.Files:N0} files · {Preview.Data.Bytes / 1048576d:N1} MiB · {Preview.Registry.Values} registry values";
        Describe();
        var browse = new Button { Content = "Include a custom DepthStrap installation…", Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(browse);
        panel.Children.Add(new TextBlock { Text = "This covers the current Windows user's listed locations. Other user profiles, unrelated browser cookies, Store app binaries and unlisted custom Roblox installations are outside this reset. Close Roblox and Studio. Type RESET to confirm permanent deletion; no cookie backup is created.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var confirmation = new TextBox { MinHeight = 32, Padding = new Thickness(6), Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(confirmation);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var apply = new Button { Content = "Permanently delete listed Roblox data", IsEnabled = false, Padding = new Thickness(10) }; panel.Children.Add(apply);
        confirmation.TextChanged += (_, _) => apply.IsEnabled = !_loading && confirmation.Text == "RESET";
        browse.Click += async (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Select DepthStrap.exe in your custom installation folder", Filter = "DepthStrap|DepthStrap.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            _loading = true; browse.IsEnabled = false; apply.IsEnabled = false; confirmation.Text = "";
            try
            {
                var candidate = Service.IncludeManagedInstallation(picker.FileName);
                var updated = await Task.Run(() => candidate.Preview());
                var updatedRegistry = await Task.Run(registry.Preview);
                Service = candidate; Preview = new(updated, updatedRegistry);
                Describe(); status.Text = "Preview updated. Only Versions and Downloads are included from the selected installation.";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "That installation could not be included. The previous preview is retained. " + ex.Message; }
            finally { _loading = false; browse.IsEnabled = true; apply.IsEnabled = confirmation.Text == "RESET"; }
        };
        Closing += (_, e) => { if (_loading) e.Cancel = true; };
        // Recheck at the action boundary as well as disabling the button.
        apply.Click += (_, _) => { if (!_loading && confirmation.Text == "RESET") DialogResult = true; };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
