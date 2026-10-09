using System.Windows;
using System.Windows.Controls;
using Bloxstrap.Networking;
using Bloxstrap.UI.Elements.Base;

namespace Bloxstrap.UI.Elements.Dialogs;

internal sealed class SystemPerformanceDialog : WpfUiWindow
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) };
    private readonly TextBox _preview = new() { Text = "Driver-supported settings and current link speed will appear here.", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 145, Margin = new Thickness(0,12,0,12) };
    private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
    private bool _running;
    internal const string BalancedDescription = "Balanced: High performance Windows power plan. Disable supported adapter energy saving, keep RSS enabled and use automatic link-speed negotiation.";
    internal const string ExtremeDescription = "Extreme: Ultimate Performance Windows plan, 100% minimum/maximum CPU state, all cores unparked and performance energy preference on AC power where supported. Disable supported adapter interrupt moderation, flow control, packet coalescing, segmentation and checksum offloads. Use driver-reported maximum RSS queues and receive/transmit buffers; RSS CPU count stays within your available logical processors.";

    private readonly Button _restart = new() { Content = "Restart Windows…", Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,12,0,0), Visibility = Visibility.Collapsed };
    private readonly bool _duringInstall;
    internal SystemPerformanceDialog(bool duringInstall = false)
    {
        _duringInstall = duringInstall;
        Title = "DepthStrap — Windows & network performance";
        Width = 720; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BrandTheme.ApplyPopup(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        void Text(string value, double size = 13) => panel.Children.Add(new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) });
        Text("Optional Windows & network optimization", 22);
        Text("Would you like DepthStrap to apply a PowerShell performance profile? Choosing a profile requests Windows administrator approval. Skip keeps your current settings.");
        Text(BalancedDescription);
        Text(ExtremeDescription);
        Text("Extreme can increase CPU use, temperature, power consumption, packet loss and queueing delay, and reduce throughput or battery life. It does not guarantee better ping or FPS. Speed uses the fastest negotiated link shared by your adapter and router/switch; forced speeds are avoided. Unsupported options are skipped. Windows components and security services stay installed.");
        Text("Changes persist until Restore. Original settings are backed up locally before changes. Restore or switching profiles restores values still owned by DepthStrap and preserves later manual changes. Adapter changes are staged without interrupting the connection; restart Windows afterward. Close Roblox and Studio first.");
        panel.Children.Add(_preview);
        var notice = new StackPanel();
        notice.Children.Add(new TextBlock { Text = "Changes will apply after Windows restart.", FontSize = 18, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        notice.Children.Add(new TextBlock { Text = duringInstall ? "Finish installation and network setup first. A Restart Windows button will appear on the installation complete screen after changes are applied." : "Adapter changes require a restart; the Windows power plan may take effect immediately. Save your work before restarting.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,0) });
        panel.Children.Add(new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(45, 230, 155, 70)), BorderBrush = System.Windows.Media.Brushes.DarkOrange, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Margin = new Thickness(0,0,0,12), Child = notice });
        foreach (var action in new[] { ("Balanced", SystemTuningMode.Balanced), ("Extreme", SystemTuningMode.Extreme), ("Restore", SystemTuningMode.Restore) })
        {
            var button = new Button { Content = action.Item1, Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,8,0) };
            button.Click += async (_, _) => await ApplyAsync(action.Item2);
            _actions.Children.Add(button);
        }
        var close = new Button { Content = "Skip / close", Padding = new Thickness(12,6,12,6) };
        close.Click += (_, _) => Close(); _actions.Children.Add(close);
        panel.Children.Add(_actions); panel.Children.Add(_status);
        _restart.Click += (_, _) => { if (SystemPerformanceTuning.ConfirmRestart()) Close(); };
        panel.Children.Add(_restart);
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Content = BrandTheme.PopupSurface(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += async (_, _) =>
        {
            _preview.Text = "Checking active physical adapters and driver-supported limits…";
            try
            {
                var adapters = await SystemPerformanceTuning.InspectAsync();
                _preview.Text = adapters.Length == 0 ? "No active physical adapter was detected. Windows power-plan tuning remains available." :
                    string.Join("\n\n", adapters.Select(x => $"{x.Adapter} · current link {x.LinkSpeed} · {x.Profile}\n" + string.Join("\n", x.Settings)));
            }
            catch { _preview.Text = "Driver limits could not be inspected. The elevated step will recheck support and skip unknown limits."; }
        };
        Closing += (_, e) => { if (_running) { e.Cancel = true; _status.Text = "Wait for the administrator-approved operation to finish. Backups are retained if a setting fails."; } };
    }
    private async Task ApplyAsync(SystemTuningMode mode)
    {
        _running = true; _actions.IsEnabled = false; _restart.IsEnabled = false;
        _status.Text = "Waiting for administrator approval and applying verified supported settings…";
        try { _status.Text = await SystemPerformanceTuning.ApplyAsync(mode); }
        finally { _running = false; _actions.IsEnabled = true; _restart.IsEnabled = true; _restart.Visibility = !_duringInstall && SystemPerformanceTuning.RestartRecommended ? Visibility.Visible : Visibility.Collapsed; }
    }
}
