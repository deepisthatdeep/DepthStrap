namespace Bloxstrap.Roblox;

/// <summary>Clean up one lingering Player only after its previously observed window was destroyed.</summary>
internal sealed class PlayerWindowLifetime
{
    private bool _seenWindow, _reported;
    private TimeSpan? _missingSince;
    private bool _destroyed;
    internal bool ShouldTerminate(TimeSpan elapsed) => _destroyed && _missingSince is TimeSpan since && elapsed - since >= TimeSpan.FromSeconds(30);
    internal string? Observe(TimeSpan elapsed, bool? hasWindow, bool previousWindowDestroyed = false)
    {
        if (hasWindow is null) { _missingSince = null; _destroyed = false; return null; }
        if (hasWindow.Value)
        {
            bool recovered = _reported;
            _seenWindow = true; _missingSince = null; _reported = false; _destroyed = false;
            return recovered ? "Player window returned; client was left running." : null;
        }
        if (!_seenWindow) return null;
        _destroyed |= previousWindowDestroyed;
        _missingSince ??= elapsed;
        if (_reported || elapsed - _missingSince < TimeSpan.FromSeconds(15)) return null;
        _reported = true;
        return "Player window has been absent for 15 seconds while the same process is still running. Checking for a destroyed window before cleanup.";
    }

    internal static async Task MonitorAsync(Process player, CancellationToken token)
    {
        var state = new PlayerWindowLifetime(); var clock = Stopwatch.StartNew();
        IntPtr lastWindow = IntPtr.Zero;
        try
        {
            if (player.ProcessName != "RobloxPlayerBeta") return;
            DateTime started = player.StartTime;
            while (!token.IsCancellationRequested && !player.HasExited)
            {
                bool? visible;
                bool destroyed = false;
                try
                {
                    player.Refresh();
                    IntPtr window = player.MainWindowHandle;
                    visible = window != IntPtr.Zero;
                    if (visible.Value) lastWindow = window;
                    else destroyed = lastWindow != IntPtr.Zero && !IsWindow(lastWindow);
                }
                catch (System.ComponentModel.Win32Exception) { visible = null; }
                string? message = state.Observe(clock.Elapsed, visible, destroyed);
                if (message is not null) App.Logger.WriteLine("Watcher::WindowLifetime", message + " Previously observed window destroyed: " + destroyed);
                if (state.ShouldTerminate(clock.Elapsed))
                {
                    token.ThrowIfCancellationRequested();
                    player.Refresh();
                    // A minimized/hidden window still exists. A new or recovered
                    // window, a failed inspection or another client cancels cleanup.
                    if (!player.HasExited && player.StartTime == started && player.MainWindowHandle == IntPtr.Zero &&
                        lastWindow != IntPtr.Zero && !IsWindow(lastWindow) && !HasVisibleWindow(player.Id))
                    {
                        player.Kill(); // Only this watched process; never its tree or other clients.
                        App.Logger.WriteLine("Watcher::WindowLifetime", "Closed the lingering Player after its window was destroyed and absent for 30 seconds.");
                        return;
                    }
                }
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { App.Logger.WriteLine("Watcher::WindowLifetime", "Window inspection or cleanup ended: " + ex.Message); }
    }

    private static bool HasVisibleWindow(int pid)
    {
        bool found = false;
        bool complete = EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == pid && IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero) found = true;
            return true;
        }, IntPtr.Zero);
        return !complete || found; // An enumeration failure must never authorize cleanup.
    }
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
}
