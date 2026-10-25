using System.ComponentModel;

namespace Bloxstrap.Utility;

internal static class PostLaunchProcess
{
    internal static bool TryParseIdentity(string? value, out int processId, out long? started)
    {
        processId = 0;
        started = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split(':');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out processId) || processId <= 0) return false;
        if (parts.Length == 1) return true;
        if (!long.TryParse(parts[1], out long ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return false;
        started = ticks;
        return true;
    }

    internal static Process? Open(int processId, string expectedName, long? started = null)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            // Retain the original process handle before waiting so a reused PID cannot become the target.
            _ = process.Handle;
            if (!process.HasExited && string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase) &&
                (started is null || process.StartTime.ToUniversalTime().Ticks == started))
                return process;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 87 or 1168) { }
        catch { process?.Dispose(); throw; }
        process?.Dispose();
        return null;
    }
}
