using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DepthStrap.Toolkit;

namespace DepthStrap.Recovery;

internal static class MacControls
{
    private sealed record Selection(Guid Id, string Name, bool Supported, string Backend, string Reason, bool Physical)
    { public override string ToString() => Name + (Supported ? " — " + Backend : " — inspection only"); }
    internal static async Task ShowAsync(Window owner)
    {
        string raw = await WindowsMacBackend.RunAsync("Inventory", Guid.Empty, null, CancellationToken.None);
        CreateDialog(owner, raw).ShowDialog();
    }
    internal static Window CreateDialog(Window owner, string raw)
    {
        var adapters = JsonSerializer.Deserialize<Selection[]>(raw) ?? Array.Empty<Selection>();
        var dialog = new Window { Owner = owner, Title = "Spoof MAC address — Beta", Width = 740, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(28, 17, 19)), Foreground = Brushes.WhiteSmoke };
        RecoveryTheme.Apply(dialog);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Choose one adapter. A MAC change restarts it and interrupts its connection. The original setting is saved in protected local storage. Close Roblox and Studio first. No third-party licensing or compatibility is changed by this tool.", TextWrapping = TextWrapping.Wrap });
        var picker = new ComboBox { ItemsSource = adapters, Margin = new Thickness(0, 16, 0, 12), MinWidth = 400 }; panel.Children.Add(picker);
        var address = new TextBox { Text = MacChange.GenerateLocalAddress(), Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(address);
        var consent = new CheckBox { Content = "I accept the connection interruption for the selected adapter." }; panel.Children.Add(consent);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(status);
        picker.SelectionChanged += (_, _) => status.Text = picker.SelectedItem is Selection selected
            ? selected.Supported ? "Driver support advertised through " + selected.Backend + ". Actual changes still require verification." : selected.Reason
            : "Select an adapter.";
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(actions);
        var apply = new Button { Content = "Spoof MAC address…", Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 0) };
        var restore = new Button { Content = "Restore original MAC…", Padding = new Thickness(10) }; actions.Children.Add(apply); actions.Children.Add(restore);
        async Task Run(bool restoring)
        {
            if (picker.SelectedItem is not Selection selected)
            { status.Text = "Select the adapter first."; return; }
            if (!selected.Supported)
            {
                status.Text = selected.Reason + (restoring
                    ? " Its original backup is retained. If this adapter is disabled or disconnected, enable or reconnect it in Windows, then reopen this panel and select Restore."
                    : " No MAC change was attempted.");
                return;
            }
            if (consent.IsChecked != true)
            { status.Text = "Confirm the selected adapter's connection interruption first."; return; }
            string? requested = restoring ? null : MacChange.NormalizeAddress(address.Text);
            if (!restoring && !MacChange.IsLocalUnicast(requested!)) { status.Text = "Use a locally administered unicast MAC."; return; }
            if (CookieReset.ClientsRunning()) { status.Text = "Close all Roblox and Studio clients first."; return; }
            if (RecoveryPrompt.Show(dialog, $"{(restoring ? "Restore" : "Change")} the MAC for {selected.Name}? Windows will request administrator approval. Its network connection will restart.",
                "Adapter MAC change", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            dialog.IsEnabled = false;
            try
            {
                var start = HelperStartInfo(restoring, selected.Id, requested);
                using var process = Process.Start(start) ?? throw new IOException("The administrator step could not start.");
                await process.WaitForExitAsync();
                status.Text = process.ExitCode == 0 ? "The administrator step finished. Its result was shown separately. Close and reopen this panel to refresh the adapter list." : "The administrator step did not complete successfully. Retained backups are available through Restore.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { status.Text = "Administrator approval was cancelled. No MAC commands ran."; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "The MAC operation could not finish. Existing backups are retained."; }
            finally { dialog.IsEnabled = true; }
        }
        apply.Click += async (_, _) => await RunClickAsync(false, () => Run(false), message => status.Text = message);
        restore.Click += async (_, _) => await RunClickAsync(true, () => Run(true), message => status.Text = message);
        dialog.Closing += (_, e) => { if (!dialog.IsEnabled) e.Cancel = true; };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; return dialog;
    }
    // Catch failures across the whole async click, including prerequisites and
    // prompts before the administrator helper's own error boundary is entered.
    internal static async Task RunClickAsync(bool restoring, Func<Task> run, Action<string> report)
    {
        try { await run(); }
        catch (ArgumentException) when (!restoring) { report("Enter a complete six-byte MAC address."); }
        catch (OperationCanceledException) { report("The operation was cancelled. Existing backups are retained; no further MAC commands were requested."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { report("The MAC " + (restoring ? "restore" : "operation") + " could not finish. Existing backups are retained. Close Roblox and Studio, including their launchers, and retry Restore."); }
    }
    internal static ProcessStartInfo HelperStartInfo(bool restoring, Guid adapter, string? address, string? executableOverride = null)
    {
        var request = MacHelperRequest.Parse(restoring ? new[] { "--mac-restore", adapter.ToString("D") }
            : new[] { "--mac-apply", adapter.ToString("D"), address! });
        string executable = executableOverride ?? Bloxstrap.Paths.Process;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new IOException("The DepthStrap app executable is unavailable. Reinstall DepthStrap before changing adapters.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in request.Arguments()) start.ArgumentList.Add(argument);
        return start;
    }
    internal static void RunHelper(string[] args)
    {
        try
        {
            var request = MacHelperRequest.Parse(args);
            RecoveryTheme.InitializeHelperAppearance(Bloxstrap.Paths.Process);
            if (!WindowsMacBackend.IsAdministrator() || CookieReset.ClientsRunning()) throw new InvalidOperationException("Administrator approval and closed Roblox/Studio clients are required.");
            var change = new MacChange(new WindowsMacBackend());
            // Startup runs on WPF's dispatcher. Keep backend continuations off
            // that thread while the synchronous helper waits for a result.
            var result = ExecuteHelperOperationAsync(request, change).GetAwaiter().GetResult();
            if (result.State == MacResultState.RestoreOffered)
            {
                if (RecoveryPrompt.Show(result.Message, "DepthStrap connection check", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes)
                    result = ExecuteHelperOperationAsync(request with { Restoring = true, Address = null }, change).GetAwaiter().GetResult();
            }
            RecoveryPrompt.Show(result.Message, "DepthStrap MAC result", MessageBoxButton.OK, result.State is MacResultState.Applied or MacResultState.Restored ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Environment.ExitCode = result.State is MacResultState.Applied or MacResultState.Restored ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { RecoveryPrompt.Show("The MAC operation could not finish. Existing original backups were retained. Close Roblox/Studio and retry Restore. No firmware identifiers were changed.", "DepthStrap MAC result", MessageBoxButton.OK, MessageBoxImage.Warning); Environment.ExitCode = 1; }
    }
    internal static Task<MacResult> ExecuteHelperOperationAsync(MacHelperRequest request, MacChange change) => Task.Run(async () =>
        request.Restoring ? await change.RestoreAsync(request.Adapter, true)
            : await change.ApplyAsync(request.Adapter, request.Address!, true));
}
