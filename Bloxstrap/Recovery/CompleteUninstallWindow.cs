using System.Windows;
using System.Windows.Controls;

namespace DepthStrap.Recovery;

internal sealed class CompleteUninstallWindow : Window
{
    internal Task ActionTask { get; private set; } = Task.CompletedTask;
    private readonly Func<RobloxCompleteUninstall> _factory;
    private readonly Func<RobloxProcessIdentity[]> _processes;
    private readonly Action<RobloxProcessIdentity[], bool> _close;
    private RobloxCompleteUninstall? _service;
    private bool _busy, _queued, _completed;
    internal CompleteUninstallWindow(Func<RobloxCompleteUninstall> factory,
        Func<RobloxProcessIdentity[]>? processes = null, Action<RobloxProcessIdentity[], bool>? close = null)
    {
        _factory = factory; _processes = processes ?? RobloxProcessCleanup.Inventory; _close = close ?? RobloxProcessCleanup.Close;
        Title = "DepthStrap " + Bloxstrap.App.Version + " — Complete wipe"; Width = 850; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RecoveryTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Text(string text, double size = 14) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        Text("Completely wipe Roblox, Studio and DepthStrap", 24);
        Text(WindowsMacBackend.IsAdministrator() ? "Administrator mode — DepthStrap " + Bloxstrap.App.Version : "Fixture mode — a live wipe requires Windows administrator approval.");
        Text("Permanently uninstall Roblox and Studio for every registered Windows user and shared installation. Delete their local data, registry entries, RobloxCookies.dat, DepthStrap settings/builds/logs/backups, discovered saved Studio projects/models, and Roblox website cookies in supported browsers. Only the exact original installed DepthStrap.exe is kept. Other website cookies stay intact. Deleting backups does not restore Windows or adapter settings. Close affected apps and supported browsers first.");
        Text("Clicking Complete wipe checks the files and permissions, then deletes the identified data in the same operation. No separate preview or saved preview is required. Unlisted copies/custom browser locations, Windows logs, external backups and cloud data are outside this wipe.");
        var closeButton = new Button { Content = "Close remaining Roblox / Studio / DepthStrap / browser processes…", Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(closeButton);
        var output = new TextBox { IsReadOnly = true, Height = 220, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed }; panel.Children.Add(output);
        var accept = new CheckBox { Content = new TextBlock { Text = "I accept permanent deletion for every Windows user, including saved Studio creations and Roblox browser cookies.", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 8) }; panel.Children.Add(accept);
        Text("Type WIPE ROBLOX to confirm permanent deletion.");
        var typed = new TextBox { MinHeight = 30 }; panel.Children.Add(typed);
        var apply = new Button { Content = "Complete wipe — permanently uninstall and delete", IsEnabled = false, Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(apply);
        var status = new TextBlock { Text = "Ready. No wipe has started.", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        bool Confirmed() => accept.IsChecked == true && typed.Text.Trim().Equals("WIPE ROBLOX", StringComparison.OrdinalIgnoreCase);
        void Update() => apply.IsEnabled = !_busy && !_completed && Confirmed();
        typed.TextChanged += (_, _) => Update(); accept.Checked += (_, _) => Update(); accept.Unchecked += (_, _) => Update();
        async Task Run(Func<Task> action)
        {
            if (_busy) return;
            _busy = true; closeButton.IsEnabled = accept.IsEnabled = typed.IsEnabled = false; Update(); status.Text = "Working…";
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                output.Visibility = Visibility.Visible; output.Text = ex.Message;
                status.Text = "Wipe could not finish: " + ex.Message + "\nResolve this issue, then confirm and retry.";
                _ = Dispatcher.InvokeAsync(() => status.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            finally { _busy = false; closeButton.IsEnabled = true; accept.IsEnabled = typed.IsEnabled = !_completed; Update(); }
        }
        void Bind(Button button, Func<Task> action) => button.Click += async (_, _) =>
        {
            if (_busy || _queued) return;
            _queued = true;
            try { ActionTask = Dispatcher.InvokeAsync(() => Run(action)).Task.Unwrap(); await ActionTask; }
            finally { _queued = false; }
        };
        Bind(closeButton, async () =>
        {
            typed.Clear(); accept.IsChecked = false; _service?.Dispose(); _service = null;
            var items = await Task.Run(_processes);
            if (items.Length == 0) { status.Text = "No remaining verified Roblox/Studio/DepthStrap processes."; return; }
            if (RecoveryPrompt.Show(this, "Close these processes in all Windows sessions? Active games, unsaved Studio work and browser tabs/form edits can be lost.\n\n"
                + string.Join("\n", items.Select(item => item.Name + " (PID " + item.Id + ")")), "Close remaining clients", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { status.Text = "Cancelled. No processes were closed."; return; }
            await Task.Run(() => _close(items, true)); status.Text = "Listed processes closed. Confirm Complete wipe when ready.";
        });
        Bind(apply, async () =>
        {
            if (!Confirmed() || _completed) return;
            // Consume confirmation before any await, including factory/preparation failures.
            typed.Clear(); accept.IsChecked = false; output.Visibility = Visibility.Visible; output.Text = "Checking the supported local wipe scope and Windows permissions…";
            try
            {
                status.Text = "Checking Windows profiles and registry access…";
                _service = await Task.Run(_factory);
                _service.Progress = new Progress<string>(message => status.Text = message);
                var approved = await Task.Run(_service.PreviewAsync);
                var result = await Task.Run(() => _service.ExecuteAsync(approved, true));
                output.Text = result.Message + $"\nRemoved {result.RemovedFiles:N0} files and {result.RemovedDirectories:N0} folders.";
                _completed = result.Complete;
                status.Text = result.Complete ? "Complete wipe finished for the supported local scope. Close this window."
                    : "Wipe incomplete. " + result.Message + "\nResolve the reported issues, then confirm and retry.";
                if (!result.Complete) _ = Dispatcher.InvokeAsync(() => status.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            finally { _service?.Dispose(); _service = null; }
        });
        Closing += (_, e) => { if (_busy || _queued) e.Cancel = true; };
        Closed += (_, _) => _service?.Dispose();
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

internal static class CompleteUninstallControls
{
    internal const string Flag = "--complete-roblox-uninstall";
    internal static void ValidateArguments(string[] args)
    { if (args.Length != 1 || args[0] != Flag) throw new ArgumentException("Invalid complete-uninstall request."); }
    internal static ProcessStartInfo StartInfo(string installedExecutable)
    {
        RobloxWipeCatalog.InstallationDirectory(installedExecutable);
        if (!File.Exists(installedExecutable) || FileVersionInfo.GetVersionInfo(installedExecutable).ProductName != "DepthStrap")
            throw new IOException("The original installed DepthStrap.exe is unavailable. Reinstall the app before using complete uninstall.");
        string running = Bloxstrap.Paths.Process;
        if (!Path.GetFullPath(running).Equals(Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase)
            && File.Exists(running) && FileVersionInfo.GetVersionInfo(running).ProductName == "DepthStrap")
        {
            using var source = File.OpenRead(running); using var installed = File.OpenRead(installedExecutable);
            if (!System.Security.Cryptography.SHA256.HashData(source).SequenceEqual(System.Security.Cryptography.SHA256.HashData(installed)))
                throw new IOException("The running DepthStrap build differs from the installed copy. Finish installing this build and reopen Anti Api so Windows elevates the matching reset helper.");
        }
        var start = new ProcessStartInfo(installedExecutable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installedExecutable)! };
        start.ArgumentList.Add(Flag); return start;
    }
    internal static Task ShowAsync(Window owner, Action<string> report, string? installedExecutable,
        Func<ProcessStartInfo, Process?>? startProcess = null, Action? closeCurrentApp = null)
    {
        if (RecoveryPrompt.Show(owner, "Request Windows administrator approval for Complete wipe and close this DepthStrap session?\n\nThe elevated window has one confirmed action for all-user app files, registry entries and Roblox browser cookies. It also permanently deletes discovered saved Studio projects/models. Only the original installed DepthStrap.exe is kept. Opening the window deletes nothing.", "Complete wipe", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { report("Cancelled. No uninstall helper was opened."); return Task.CompletedTask; }
        try
        {
            using var process = (startProcess ?? Process.Start)(StartInfo(installedExecutable ?? Bloxstrap.Paths.Application)) ?? throw new IOException("The administrator reset window could not open.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { report("Windows administrator approval was cancelled. No files, registry entries or browser cookies were changed."); return Task.CompletedTask; }
        Bloxstrap.App.CompleteUninstallHandoff = true;
        if (closeCurrentApp is not null) closeCurrentApp();
        else Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()), System.Windows.Threading.DispatcherPriority.Background);
        return Task.CompletedTask;
    }
}
