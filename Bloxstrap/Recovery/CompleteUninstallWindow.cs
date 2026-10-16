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
    private CompleteUninstallPreview? _preview;
    private bool _busy;
    internal CompleteUninstallWindow(Func<RobloxCompleteUninstall> factory,
        Func<RobloxProcessIdentity[]>? processes = null, Action<RobloxProcessIdentity[], bool>? close = null)
    {
        _factory = factory; _processes = processes ?? RobloxProcessCleanup.Inventory; _close = close ?? RobloxProcessCleanup.Close;
        Title = "DepthStrap — Complete Roblox reset"; Width = 850; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RecoveryTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Text(string text, double size = 14) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        Text("Complete Roblox reset", 24);
        Text(WindowsMacBackend.IsAdministrator() ? "Administrator mode — files, registry and browser cookies use the same approved reset." : "Test preview — live resets must be opened through Windows administrator approval.");
        Text("One reset for every registered Windows user: Roblox and Studio uninstall, registry keys, RobloxCookies.dat, DepthStrap data and Roblox website cookies in supported browser profiles. This also deletes previewed saved Studio creations. Only the original installed DepthStrap.exe is kept. Other website cookies stay intact. Deleting backups does not restore adapter settings. Close Roblox, Studio, other DepthStrap windows and supported browsers before previewing.");
        var closeButton = new Button { Content = "Close remaining Roblox / Studio / DepthStrap / browser processes…", Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(closeButton);
        var scan = new Button { Content = "Create uninstall preview", Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(scan);
        var output = new TextBox { IsReadOnly = true, Height = 260, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(output);
        var accept = new CheckBox { Content = new TextBlock { Text = "I accept permanent deletion for every Windows user, including saved Studio creations and Roblox browser cookies.", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 14, 0, 8) }; panel.Children.Add(accept);
        Text("Type RESET ROBLOX to confirm the preview.");
        var typed = new TextBox { MinHeight = 30 }; panel.Children.Add(typed);
        var apply = new Button { Content = "Confirm complete reset — permanently delete previewed data", IsEnabled = false, Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(apply);
        var status = new TextBlock { Text = "Create a preview. No uninstall has started.", TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        bool Confirmed() => accept.IsChecked == true && typed.Text.Trim().Equals("RESET ROBLOX", StringComparison.OrdinalIgnoreCase);
        void Update() => apply.IsEnabled = !_busy && _preview is not null && Confirmed();
        typed.TextChanged += (_, _) => Update(); accept.Checked += (_, _) => Update(); accept.Unchecked += (_, _) => Update();
        async Task Run(Func<Task> action)
        {
            if (_busy) return;
            _busy = true; scan.IsEnabled = closeButton.IsEnabled = accept.IsEnabled = typed.IsEnabled = false; Update(); status.Text = "Working…";
            try { await action(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _preview = null; output.Text = ex.Message; status.Text = "Could not finish. Review the error and create a new preview."; }
            finally { _busy = false; scan.IsEnabled = closeButton.IsEnabled = accept.IsEnabled = typed.IsEnabled = true; Update(); }
        }
        void Bind(Button button, Func<Task> action) => button.Click += async (_, _) =>
        { ActionTask = Dispatcher.InvokeAsync(() => Run(action)).Task.Unwrap(); await ActionTask; };
        Bind(closeButton, async () =>
        {
            _preview = null; typed.Clear(); accept.IsChecked = false; _service?.Dispose(); _service = null;
            var items = await Task.Run(_processes);
            if (items.Length == 0) { status.Text = "No remaining verified Roblox/Studio/DepthStrap processes."; return; }
            if (RecoveryPrompt.Show(this, "Close these processes in all Windows sessions? Active games, unsaved Studio work and browser tabs/form edits can be lost.\n\n"
                + string.Join("\n", items.Select(item => item.Name + " (PID " + item.Id + ")")), "Close remaining clients", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { status.Text = "Cancelled. No processes were closed."; return; }
            await Task.Run(() => _close(items, true)); status.Text = "Listed processes closed. Create a new uninstall preview.";
        });
        Bind(scan, async () =>
        {
            _preview = null; typed.Clear(); accept.IsChecked = false; _service?.Dispose(); _service = null;
            _service = await Task.Run(_factory);
            _service.Progress = new Progress<string>(message => status.Text = message);
            _preview = await Task.Run(_service.PreviewAsync);
            output.Text = $"{_preview.Files.Files:N0} files · {_preview.Files.Bytes / 1048576d:N1} MiB · {_preview.Profiles.Length} Windows profiles\n"
                + "KEEP: " + (_preview.PreservedExecutable ?? "No original executable exists") + "\n\nDELETE LOCATIONS:\n"
                + string.Join("\n", _preview.Catalog.Locations) + "\n\nREGISTRY:\n" + string.Join("\n", _preview.Registry.Select(item => item.Preview.Location))
                + "\n\nSAVED STUDIO FILES TO DELETE:\n" + string.Join("\n", _preview.Catalog.Projects)
                + "\n\nWINDOWS STORE PACKAGES:\n" + string.Join("\n", _preview.Store.Installed.Concat(_preview.Store.Provisioned))
                + "\n\nPACKAGE DATA REMOVED BY WINDOWS (excluded from file counts):\n" + string.Join("\n", _preview.ServicedStoreLocations)
                + $"\n\nROBLOX BROWSER COOKIES ({_preview.BrowserCookies.Databases.Sum(entry => entry.Cookies):N0}):\n"
                + string.Join("\n", _preview.BrowserCookies.Databases.Select(entry => entry.Target.Browser + " · " + entry.Cookies + " cookies · " + entry.Target.Path))
                + "\n\nSKIPPED / UNRESOLVED:\n" + string.Join("\n", _preview.Catalog.Skipped)
                + "\n\nCustom browser locations not listed, Windows logs, external backups and cloud data are outside this reset.";
            status.Text = _preview.Catalog.Skipped.Length == 0 ? "Preview ready. Review every location before confirming." : "Preview ready with skipped locations. These prevent a complete-wipe result.";
        });
        Bind(apply, async () =>
        {
            if (_preview is null || _service is null || !Confirmed()) return;
            var approved = _preview; _preview = null;
            var result = await Task.Run(() => _service.ExecuteAsync(approved, true));
            output.Text = result.Message + $"\nRemoved {result.RemovedFiles:N0} files and {result.RemovedDirectories:N0} folders.";
            status.Text = result.Complete ? "Uninstall completed for the previewed scope. Close this window." : "Uninstall incomplete. Review the remaining issues before retrying.";
            typed.Clear(); accept.IsChecked = false; _service.Dispose(); _service = null;
        });
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
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
        if (RecoveryPrompt.Show(owner, "Request Windows administrator approval for the complete reset and close this DepthStrap session?\n\nFiles, registry entries and Roblox browser cookies are handled together. You will review one all-user deletion preview and confirm it in the elevated window. Opening it deletes nothing.", "Complete Roblox reset", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
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
