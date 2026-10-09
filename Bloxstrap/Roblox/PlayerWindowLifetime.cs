namespace Bloxstrap.Roblox;

/// <summary>Diagnostic-only window lifetime tracking; absence of a window is not permission to kill a client.</summary>
internal sealed class PlayerWindowLifetime
{
    private bool _seenWindow, _reported;
    private TimeSpan? _missingSince;
    internal string? Observe(TimeSpan elapsed, bool? hasWindow)
    {
        if (hasWindow is null) { _missingSince = null; return null; }
        if (hasWindow.Value)
        {
            bool recovered = _reported;
            _seenWindow = true; _missingSince = null; _reported = false;
            return recovered ? "Player window returned; client was left running." : null;
        }
        if (!_seenWindow) return null;
        _missingSince ??= elapsed;
        if (_reported || elapsed - _missingSince < TimeSpan.FromSeconds(15)) return null;
        _reported = true;
        return "Player window has been absent for 15 seconds while the same process is still running. No termination or relaunch was requested.";
    }

    internal static async Task MonitorAsync(Process player, CancellationToken token)
    {
        var state = new PlayerWindowLifetime(); var clock = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested && !player.HasExited)
            {
                bool? visible;
                try { player.Refresh(); visible = player.MainWindowHandle != IntPtr.Zero; }
                catch (System.ComponentModel.Win32Exception) { visible = null; }
                string? message = state.Observe(clock.Elapsed, visible);
                if (message is not null) App.Logger.WriteLine("Watcher::WindowLifetime", message);
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { App.Logger.WriteLine("Watcher::WindowLifetime", "Window inspection ended; no process action was taken."); }
    }
}
