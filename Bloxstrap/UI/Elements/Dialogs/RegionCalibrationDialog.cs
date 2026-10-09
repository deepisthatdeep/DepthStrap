using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Bloxstrap.Networking;
using Bloxstrap.UI.Elements.Base;

namespace Bloxstrap.UI.Elements.Dialogs
{
    internal sealed class RegionCalibrationDialog : WpfUiWindow
    {
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,16,0,16) };
        private readonly CheckBox _terms = new() { Content = "I accept Cloudflare's application terms and privacy policy.", Margin = new Thickness(0,12,0,12) };
        private readonly CancellationTokenSource _cts = new();
        private readonly WrapPanel _actions = new() { Orientation = Orientation.Horizontal };
        private bool _running;
        private readonly bool _reset;
        public RegionCalibrationDialog(bool reset = false)
        {
            _reset = reset;
            Title = reset ? "DepthStrap — Reset Network" : "DepthStrap — Network setup";
            Width = 610; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            BrandTheme.ApplyPopup(this);
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = reset ? "Reset Network" : "Normal routing vs Cloudflare WARP", FontSize = 22, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0),
                Text = "Disconnect other VPNs and proxies before testing. They can skew the normal-route baseline and location estimates. DepthStrap switches WARP off and on during this test; the choice affects your computer's Internet connection. Close Roblox first." });
            if (reset) panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0),
                Text = "This clears DepthStrap's network session logs, traceroutes, region history and learned preferences, then runs a new comparison. Your other app settings are retained." });
            var links = new TextBlock { Margin = new Thickness(0,12,0,0) };
            foreach (var item in new[] { ("Cloudflare application terms", "https://www.cloudflare.com/application/terms/"), ("Privacy policy", "https://www.cloudflare.com/application/privacypolicy/") })
            {
                var link = new Hyperlink(new Run(item.Item1)) { NavigateUri = new Uri(item.Item2) };
                link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
                links.Inlines.Add(link); links.Inlines.Add(new Run("    "));
            }
            panel.Children.Add(links);
            _terms.IsChecked = App.Settings.Prop.CloudflareTermsAccepted;
            panel.Children.Add(_terms);
            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "The official Cloudflare package downloads as part of setup. Windows may ask for administrator permission. Both routes receive three passes with 24 probes per published IPv4/IPv6 target, at least 45 seconds per pass, and 15 seconds to settle after switching. Allow 6–15 minutes; unresponsive targets can take longer, up to the 45-minute test limit. Routing probes are provisional; they do not measure Roblox gameplay ping." });
            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0), Text = "Partial coverage gets one extra comparison pass. If fewer than two locations reply reliably on both routes, setup keeps your starting WARP state and explains the result. The manual WARP toggle remains available." });
            _status.Text = "Ready to test. No city is preselected. Play history will refine the measured preference.";
            panel.Children.Add(_status);
            var start = new Button { Content = "Set up WARP and compare", Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,8,0) };
            var direct = new Button { Content = "Test current route only", Padding = new Thickness(12,6,12,6) };
            start.Click += async (_, _) =>
            {
                if (_terms.IsChecked != true) { _status.Text = "Read and accept Cloudflare's terms and privacy policy to set up WARP, or test the current route only."; return; }
                App.Settings.Prop.CloudflareTermsAccepted = true;
                await RunAsync(true);
            };
            direct.Click += async (_, _) => await RunAsync(false);
            _actions.Children.Add(start); _actions.Children.Add(direct); panel.Children.Add(_actions);
            MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 40);
            MaxWidth = Math.Max(240, SystemParameters.WorkArea.Width - 40);
            Content = BrandTheme.PopupSurface(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Closing += (_, e) => { if (_running) { e.Cancel = true; _cts.Cancel(); _status.Text = "Stopping setup; completing any required route restoration…"; } };
            Closed += (_, _) => _cts.Dispose();
        }
        private async Task RunAsync(bool setupWarp)
        {
            _running = true; _actions.IsEnabled = false; _terms.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(message => _status.Text = message);
                var result = await RegionCalibrationService.RunComparisonAsync(_reset, setupWarp, progress, _cts.Token);
                Frontend.ShowMessageBox(result.Status, result.SetupFinished ? MessageBoxImage.Information : MessageBoxImage.Warning);
                _running = false; Close();
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
                App.Logger.WriteException("NetworkSetupDialog", ex);
                _running = false;
                if (_cts.IsCancellationRequested) Close();
                else { _actions.IsEnabled = true; _terms.IsEnabled = true; }
            }
        }
    }
}
