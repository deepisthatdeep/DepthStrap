using Bloxstrap.Roblox;

namespace DepthStrap.Recovery;

internal sealed record ClientInfo(bool Complete, string Version, string Architecture, string DllState);
internal static class ClientInspection
{
    internal static ClientInfo Inspect(string executable)
    {
        if (!Path.GetFileName(executable).Equals("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select RobloxPlayerBeta.exe from the installed build.");
        bool complete = RobloxClientFiles.IsPlayerComplete(executable);
        string version = "Unavailable", architecture = "Unavailable";
        try
        {
            version = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "Unavailable";
            using var stream = File.OpenRead(executable);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            architecture = pe.PEHeaders.CoffHeader.Machine.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        string dll = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "RobloxPlayerBeta.dll");
        return new(complete, version, architecture, File.Exists(dll) ? "Present; see client validation result above" : "Absent; required only by split builds");
    }
}
