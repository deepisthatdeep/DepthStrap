using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bloxstrap.UI.Elements.Dialogs;

internal static class ExecutorNoticeChecks
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    internal static void Run(Action<bool, string> check, Action<FrameworkElement, string, int, int> render, string output)
    {
        var clock = new Clock();
        var notice = new ExecutorCompatibilityNotice(clock) { ShowInTaskbar = false };
        clock.Ticks = TimeSpan.FromMinutes(1).Ticks;
        notice.RefreshCountdown();
        check(!notice.ContinueButton.IsEnabled, "Time before the notice is displayed cannot satisfy its reading delay");
        var content = (FrameworkElement)notice.Content;
        content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, notice.Foreground);
        content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, notice.FontFamily);
        notice.Content = null;
        render(new Border { Background = notice.Background, Child = content }, output, 640, 460);
        ((Border)content.Parent).Child = null;
        notice.Content = content;
        Exception? failure = null;
        notice.Loaded += (_, _) => notice.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                check(!notice.ContinueButton.IsEnabled, "Showing the notice starts a full five-second delay");
                notice.Close();
                check(notice.IsVisible, "Closing the notice before the delay cannot bypass installation acknowledgement");
                notice.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(notice.IsVisible, "An early acknowledgement event cannot bypass the disabled button");
                clock.Ticks += TimeSpan.FromMilliseconds(4999).Ticks;
                notice.RefreshCountdown();
                check(!notice.ContinueButton.IsEnabled, "Acknowledgement remains disabled at 4.999 seconds");
                clock.Ticks += TimeSpan.FromMilliseconds(1).Ticks;
                notice.RefreshCountdown();
                check(notice.ContinueButton.IsEnabled, "Acknowledgement becomes available at five seconds");
                notice.Close();
                check(notice.IsVisible, "Elapsed time alone does not dismiss the mandatory notice");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                clock.Ticks += TimeSpan.FromSeconds(5).Ticks;
                notice.RefreshCountdown();
                notice.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }), DispatcherPriority.ApplicationIdle);
        bool? result = notice.ShowDialog();
        if (failure is not null) throw failure;
        check(result == true && !notice.IsVisible, "Explicit acknowledgement closes the modal notice and authorizes installation");
    }

    internal static void CheckRealTimer(Action<bool, string> check)
    {
        var notice = new ExecutorCompatibilityNotice { ShowInTaskbar = false };
        var elapsed = new System.Diagnostics.Stopwatch();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        bool completed = false;
        notice.Loaded += (_, _) => { elapsed.Start(); poll.Start(); };
        poll.Tick += (_, _) =>
        {
            if (!notice.ContinueButton.IsEnabled) return;
            completed = elapsed.Elapsed >= TimeSpan.FromMilliseconds(4900);
            poll.Stop();
            notice.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        try { check(notice.ShowDialog() == true && completed, "The real dispatcher timer enforces the five-second reading interval"); }
        finally { poll.Stop(); }
    }
}
