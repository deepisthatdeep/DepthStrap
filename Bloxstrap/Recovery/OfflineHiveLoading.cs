using System.ComponentModel;

namespace DepthStrap.Recovery;

/// <summary>Real profile hives can have mixed ACLs; the application-hive loader is only a fallback.</summary>
internal static class OfflineHiveLoading
{
    internal static T Open<T>(bool administrator, Func<T?> loaded, Func<T> global, Func<T> application, string label,
        Action<int>? pause = null) where T : class
    {
        pause ??= Thread.Sleep;
        var errors = new List<(string Loader, int Code)>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Windows may mount the profile between discovery and loading.
            if (loaded() is T live) return live;
            bool busy = false;
            foreach (var loader in administrator ? new[] { ("profile", global), ("application", application) } : new[] { ("application", application), ("profile", global) })
            {
                try { return loader.Item2(); }
                catch (Win32Exception ex) { errors.Add((loader.Item1, ex.NativeErrorCode)); busy |= ex.NativeErrorCode is 32 or 33 or 170; }
                catch (UnauthorizedAccessException) { errors.Add((loader.Item1, 1314)); }
            }
            if (!busy || attempt == 2) break;
            pause(200 * (attempt + 1));
        }
        string detail = string.Join("; ", errors.Distinct().Select(item => item.Loader + " " + item.Code + " (" + new Win32Exception(item.Code).Message.Trim() + ")"));
        throw new IOException("Windows could not open " + label + ". " + detail
            + ". No wipe was started. Close affected apps and sign out other Windows users before retrying. If the hive remains locked, restart Windows and retry from the updated DepthStrap installation.");
    }
}
