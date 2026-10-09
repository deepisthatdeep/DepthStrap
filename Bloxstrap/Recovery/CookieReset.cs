using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DepthStrap.Recovery;

internal enum CookieResetResult { Removed, NotPresent }

/// <summary>Deletes exactly one local file by its verified handle; never reads cookie contents.</summary>
internal sealed class CookieReset(string robloxRoot, Func<bool> clientsRunning)
{
    private readonly string _target = ResolveTarget(robloxRoot);
    private static string ResolveTarget(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new IOException("The Roblox cookie directory is unavailable or is not an absolute path.");
        string resolved = Path.GetFullPath(root);
        if (resolved.TrimEnd('\\', '/').Equals(Path.GetPathRoot(resolved)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A drive root cannot be used as a Roblox cookie directory.");
        return Path.Combine(resolved, "RobloxCookies.dat");
    }
    internal string Target => _target;
    internal static bool ClientsRunning(Func<string, bool> exists) => new[]
        { "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxPlayerLauncher", "RobloxStudioLauncherBeta" }.Any(exists);
    internal static bool ClientsRunning()
    {
        return ClientsRunning(name =>
        {
            var processes = Process.GetProcessesByName(name);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        });
    }
    internal CookieResetResult Reset(bool confirmedSignOut, CancellationToken token = default)
    {
        if (!confirmedSignOut) throw new InvalidOperationException("Confirm that resetting cookies may sign you out of Roblox.");
        token.ThrowIfCancellationRequested();
        if (clientsRunning()) throw new InvalidOperationException("Close every Roblox Player and Studio window before resetting cookies.");
        // OPEN_REPARSE_POINT prevents following a file symlink. An exclusive handle
        // prevents replacement/renaming of the opened file before its disposition is set.
        using var file = CreateFile(NativePath(_target), 0x00010000 | 0x00000080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return CookieResetResult.NotPresent;
            throw new IOException("The cookie file is busy or inaccessible. It was left unchanged.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (0x400u | 0x10u)) != 0)
            throw new IOException("Cookie reset cannot operate on a linked file or directory.");
        var path = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (count == 0 || count >= path.Capacity) throw new IOException("The cookie path could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        string resolved = NormalizeFinalPath(path.ToString());
        if (!string.Equals(resolved, _target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cookie reset cannot follow a redirected Roblox directory.");
        token.ThrowIfCancellationRequested();
        if (clientsRunning()) throw new InvalidOperationException("A Roblox client started during reset. The cookie file was preserved.");
        token.ThrowIfCancellationRequested();
        var disposition = new FileDisposition { Flags = 1 | 0x10 }; // DELETE | IGNORE_READONLY_ATTRIBUTE
        if (!SetFileInformationByHandle(file, 21, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new IOException("Windows could not remove the cookie file. It was left unchanged.", new Win32Exception(Marshal.GetLastWin32Error()));
        return CookieResetResult.Removed; // The verified file is deleted as this handle closes.
    }
    internal static string NormalizeFinalPath(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    internal static string NativePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("A native reset path must be absolute.");
        string normalized = NormalizeFinalPath(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(normalized) || normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException("Device paths cannot be used for Roblox reset.");
        return normalized.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + normalized[2..] : @"\\?\" + normalized;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref FileDisposition info, uint size);
}
