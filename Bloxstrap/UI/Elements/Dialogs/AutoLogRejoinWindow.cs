using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bloxstrap.UI.Elements.Dialogs
{
    internal sealed class AutoLogRejoinWindow : Window
    {
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int _seconds = 10;
        internal Task<bool> Decision => _decision.Task;
        internal AutoLogRejoinWindow(Func<bool> stillOnHome)
        {
            Title = "DepthStrap — Rejoin Deepwoken"; Width = 390; SizeToContent = SizeToContent.Height;
            ShowActivated = false; ShowInTaskbar = false; Topmost = true;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            BrandTheme.ApplyPopup(this);
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "Returned to Roblox Home", FontSize = 18, FontWeight = FontWeights.SemiBold });
            var countdown = new TextBlock { Text = "Reopening Deepwoken in 10 seconds…", Margin = new Thickness(0, 12, 0, 16), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(countdown);
            var cancel = new Button { Content = "Stay on Home", Padding = new Thickness(12, 5, 12, 5) };
            cancel.Click += (_, _) => Close(); panel.Children.Add(cancel);
            Content = BrandTheme.PopupSurface(new Border { BorderBrush = (Brush)Application.Current.Resources["SystemAccentColorBrush"], BorderThickness = new Thickness(1), Child = panel });
            Loaded += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Right - ActualWidth - 20; Top = area.Bottom - ActualHeight - 20; _timer.Start(); };
            _timer.Tick += (_, _) =>
            {
                if (!stillOnHome()) { Close(); return; }
                if (--_seconds <= 0) { _decision.TrySetResult(true); Close(); return; }
                countdown.Text = $"Reopening Deepwoken in {_seconds} seconds…";
            };
            Closed += (_, _) => { _timer.Stop(); _decision.TrySetResult(false); };
        }
    }
}
