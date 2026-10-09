using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bloxstrap.UI.Elements.Base;

namespace Bloxstrap.UI.Elements.Dialogs;

internal sealed class ExecutorCompatibilityNotice : WpfUiWindow
{
    private readonly TimeProvider _clock;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long? _started;
    private bool _acknowledged;
    internal Button ContinueButton { get; } = new() { IsEnabled = false, Padding = new Thickness(16, 8, 16, 8) };
    internal const string Explanation = "Third-party executors must support the Roblox build you use. Executor-related detection, launch or disconnect issues may require a fix from the executor provider; DepthStrap cannot guarantee compatibility or resolve every third-party issue.";
    internal const string VoltExample = "Reported Volt workaround: some users needed to downgrade Roblox to a build supported by Volt, then reinstall Volt. Check the provider's current instructions first; this is a reported workaround, not a requirement for everyone.";

    internal ExecutorCompatibilityNotice(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Title = "DepthStrap — executor compatibility";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BrandTheme.ApplyPopup(this);
        var panel = new StackPanel { Margin = new Thickness(28) };
        void Text(string value, double size = 14) => panel.Children.Add(new TextBlock
        {
            Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18)
        });
        Text("Before you install DepthStrap", 24);
        Text(Explanation);
        Text(VoltExample);
        Text("This notice does not change your Roblox version or install an executor. Read it, then acknowledge it to continue installation.");
        panel.Children.Add(ContinueButton);
        Content = BrandTheme.PopupSurface(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ContinueButton.Click += (_, _) =>
        {
            RefreshCountdown();
            if (!ContinueButton.IsEnabled) return;
            _acknowledged = true;
            DialogResult = true;
        };
        Loaded += (_, _) =>
        {
            if (_started.HasValue) return;
            _started = _clock.GetTimestamp();
            RefreshCountdown();
            _timer.Start();
        };
        _timer.Tick += (_, _) => RefreshCountdown();
        Closing += (_, e) => e.Cancel = !_acknowledged;
        Closed += (_, _) => _timer.Stop();
        RefreshCountdown();
    }

    internal void RefreshCountdown()
    {
        var elapsed = _started.HasValue ? _clock.GetElapsedTime(_started.Value) : TimeSpan.Zero;
        ContinueButton.IsEnabled = _started.HasValue && elapsed >= TimeSpan.FromSeconds(5);
        ContinueButton.Content = ContinueButton.IsEnabled ? "I understand — continue installation" :
            $"Please read — continue in {Math.Max(1, (int)Math.Ceiling(5 - elapsed.TotalSeconds))}s";
        if (ContinueButton.IsEnabled) _timer.Stop();
    }
}
