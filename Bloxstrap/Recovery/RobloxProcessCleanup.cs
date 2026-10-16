using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DepthStrap.Recovery;

internal sealed record RobloxProcessIdentity(int Id, long Started, string Executable, string Name);
internal static class RobloxProcessCleanup
{
    internal static RobloxProcessIdentity[] Inventory()
    {
        var result = new List<RobloxProcessIdentity>();
        foreach (string name in RobloxWipeCatalog.ClientExecutables.Select(file => Path.GetFileNameWithoutExtension(file)!).Concat(new[] { "Windows10Universal", "Roblox" }).Concat(BrowserCookieReset.BrowserProcesses).Distinct())
            foreach (var process in Process.GetProcessesByName(name!))
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        string executable = process.MainModule?.FileName ?? throw new IOException("A running client path is unavailable.");
                        var info = FileVersionInfo.GetVersionInfo(executable);
                        bool ours = name == "DepthStrap" ? info.ProductName == "DepthStrap"
                            : BrowserCookieReset.BrowserProcesses.Contains(name) ? IsSupportedBrowser(name, info)
                            : info.CompanyName?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true;
                        if (!ours) throw new IOException("A similarly named process could not be verified as Roblox/DepthStrap; close it manually before uninstalling.");
                        result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, executable, name!));
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception ex) { throw new IOException("A remaining client cannot be inspected. Use administrator approval or close it manually.", ex); }
                }
        return result.OrderBy(item => item.Id).ToArray();
    }
    internal static void Close(RobloxProcessIdentity[] approved, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm closing the displayed processes first.");
        foreach (var item in approved)
        {
            if (item.Id == Environment.ProcessId) throw new InvalidOperationException("The uninstall helper cannot close itself.");
            if (!Path.IsPathFullyQualified(item.Executable) || !string.Equals(Path.GetFileNameWithoutExtension(item.Executable), item.Name, StringComparison.OrdinalIgnoreCase)
                || (!RobloxWipeCatalog.ClientExecutables.Contains(Path.GetFileName(item.Executable), StringComparer.OrdinalIgnoreCase)
                    && !BrowserCookieReset.BrowserProcesses.Contains(item.Name)
                    && item.Name is not ("Windows10Universal" or "Roblox"))) throw new InvalidOperationException("Only a listed Roblox, Studio, DepthStrap or supported browser process can be closed.");
            try
            {
                using var process = Process.GetProcessById(item.Id);
                if (process.HasExited) continue;
                // Keep the queried process object/handle through termination: PID reuse
                // or another application at the same path cannot retarget this action.
                if (process.StartTime.ToUniversalTime().Ticks != item.Started || !string.Equals(process.MainModule?.FileName, item.Executable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A listed process changed. Refresh before closing clients.");
                process.Kill();
                if (!process.WaitForExit(10000)) throw new IOException("A client has not finished closing.");
            }
            catch (ArgumentException) { } // Already exited.
        }
    }
    private static bool IsSupportedBrowser(string name, FileVersionInfo info)
    {
        string product = info.ProductName ?? "";
        return name switch
        {
            "chrome" => product.Contains("Chrome", StringComparison.OrdinalIgnoreCase),
            "msedge" => product.Contains("Edge", StringComparison.OrdinalIgnoreCase),
            "firefox" => product.Contains("Firefox", StringComparison.OrdinalIgnoreCase),
            "brave" => product.Contains("Brave", StringComparison.OrdinalIgnoreCase),
            "vivaldi" => product.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase),
            "opera" => product.Contains("Opera", StringComparison.OrdinalIgnoreCase),
            "waterfox" => product.Contains("Waterfox", StringComparison.OrdinalIgnoreCase),
            "librewolf" => product.Contains("LibreWolf", StringComparison.OrdinalIgnoreCase),
            "chromium" => product.Contains("Chromium", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
