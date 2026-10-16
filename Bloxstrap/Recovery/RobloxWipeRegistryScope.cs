using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DepthStrap.Recovery;

internal sealed record RobloxWipeProfile(string Sid, string Directory);

/// <summary>Owns temporary mounts for offline profiles; never loads a missing hive or accepts a hive path from a helper argument.</summary>
internal sealed class RobloxWipeRegistryScope : IDisposable
{
    internal sealed record Root(RegistryKey Key, string Label, bool ClassesOnly, RegistryView View);
    internal List<Root> Roots { get; } = new();
    private readonly List<string> _mounts = new();
    private readonly List<Privilege> _privileges = new();
    private readonly List<RegistryKey> _applicationHives = new();
    internal List<string> UnloadFailures { get; } = new();
    internal static RobloxWipeProfile[] Profiles()
    {
        using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
            ?? throw new IOException("Windows profile inventory is unavailable.");
        return list.GetSubKeyNames().Where(IsUserSid).Select(sid =>
        {
            using var key = list.OpenSubKey(sid);
            string? raw = key?.GetValue("ProfileImagePath") as string;
            string path = raw is null ? "" : Environment.ExpandEnvironmentVariables(raw);
            if (!Path.IsPathFullyQualified(path)) throw new IOException("A registered Windows profile has no absolute path.");
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (path == Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar)) throw new IOException("Invalid Windows profile root.");
            return new RobloxWipeProfile(sid, path);
        }).OrderBy(profile => profile.Sid, StringComparer.Ordinal).ToArray();
    }
    internal static bool IsUserSid(string sid) => System.Text.RegularExpressions.Regex.IsMatch(sid,
        @"\AS-1-(?:5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+|12-1-[0-9]+-[0-9]+-[0-9]+-[0-9]+)\z");

    internal static RobloxWipeRegistryScope Open(RobloxWipeProfile[] profiles)
    {
        if (!WindowsMacBackend.IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required for the all-user wipe.");
        var scope = new RobloxWipeRegistryScope();
        try
        {
            foreach (var profile in profiles)
            {
                scope.OpenHive(profile.Sid, Path.Combine(profile.Directory, "NTUSER.DAT"), profile.Sid, false);
                scope.OpenHive(profile.Sid + "_Classes", Path.Combine(profile.Directory, "AppData", "Local", "Microsoft", "Windows", "UsrClass.dat"), profile.Sid + " classes", true);
            }
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                scope.Roots.Add(new(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view), "HKLM (" + view + ")", false, view));
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
    private void OpenHive(string sid, string file, string label, bool classes)
    {
        var loaded = Registry.Users.OpenSubKey(sid, writable: true);
        if (loaded is not null) { Roots.Add(new(loaded, "HKU\\" + label, classes, RegistryView.Default)); return; }
        try { File.GetAttributes(file); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        // Reject redirected profiles/hive files before calling Windows' loader.
        for (string? path = file; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A profile hive is redirected; nothing can be wiped until its location is reviewed.");
        // A data-reading FileStream conflicts with Windows' hive loader. An
        // attributes-only handle records identity without claiming read-data
        // sharing rights. Recheck it after load before exposing any registry key.
        using var existing = HiveFile(file);
        if (!GetFileInformationByHandle(existing, out var before)) throw new IOException("The offline hive identity is unavailable.");
        // Prefer the handle-only loader: it avoids global HKU mount names and
        // automatically unloads when the last handle closes. The legacy global
        // loader can fail even with elevation on supported Windows setups.
        int applicationStatus = RegLoadAppKey(file, out var applicationHandle, 0xf003f, 1, 0);
        if (applicationStatus == 0)
        {
            try { ValidateHiveFile(file, existing, before); }
            catch { applicationHandle.Dispose(); throw; }
            var applicationKey = RegistryKey.FromHandle(applicationHandle);
            _applicationHives.Add(applicationKey);
            Roots.Add(new(applicationKey, "Profile " + label + " (offline)", classes, RegistryView.Default));
            return;
        }
        applicationHandle.Dispose();
        if (_privileges.Count == 0) { _privileges.Add(new("SeBackupPrivilege")); _privileges.Add(new("SeRestorePrivilege")); }
        string mount = "DepthStrapWipe-" + Guid.NewGuid().ToString("N");
        int status = RegLoadKey(new IntPtr(unchecked((int)0x80000003)), mount, file);
        if (status != 0) throw new IOException("An offline Windows profile hive could not be opened (handle loader " + applicationStatus + ", global loader " + status + "). No deletion was started.", new Win32Exception(status));
        _mounts.Add(mount);
        ValidateHiveFile(file, existing, before);
        var root = Registry.Users.OpenSubKey(mount, writable: true) ?? throw new IOException("The temporary profile hive is unavailable.");
        Roots.Add(new(root, "HKU\\" + label + " (offline)", classes, RegistryView.Default));
    }
    private static SafeFileHandle HiveFile(string file)
    {
        var handle = CreateFile(CookieReset.NativePath(file), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("An existing offline hive could not be opened for identity verification."); }
        return handle;
    }
    private static void ValidateHiveFile(string file, SafeFileHandle original, HiveInformation before)
    {
        using var current = HiveFile(file);
        if (!GetFileInformationByHandle(current, out var after) || before.Volume != after.Volume || before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow
            || (after.Attributes & 0x400) != 0) throw new IOException("An offline profile hive changed or became redirected during loading.");
        foreach (var handle in new[] { original, current })
        {
            var path = new StringBuilder(32768); uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity || !CookieReset.NormalizeFinalPath(path.ToString()).Equals(file, StringComparison.OrdinalIgnoreCase))
                throw new IOException("An offline profile hive path changed during loading.");
        }
    }
    public void Dispose()
    {
        foreach (var key in _applicationHives)
        {
            int status = RegFlushKey(key.Handle);
            if (status != 0) UnloadFailures.Add("Offline profile changes could not be flushed (Windows status " + status + ").");
        }
        _applicationHives.Clear();
        foreach (var root in Roots) root.Key.Dispose(); Roots.Clear();
        foreach (string mount in _mounts.AsEnumerable().Reverse())
        {
            int status = RegUnLoadKey(new IntPtr(unchecked((int)0x80000003)), mount);
            if (status != 0) { UnloadFailures.Add("Temporary profile hive unload did not finish (Windows status " + status + ")."); Bloxstrap.App.Logger.WriteLine("RobloxWipe", "Temporary hive unload did not finish: " + status); }
        }
        _mounts.Clear();
        foreach (var privilege in _privileges.AsEnumerable().Reverse()) privilege.Dispose(); _privileges.Clear();
    }
    internal sealed class Privilege : IDisposable
    {
        private readonly SafeAccessTokenHandle _token;
        private TokenPrivilege _previous;
        internal Privilege(string name)
        {
            if (!OpenProcessToken(new IntPtr(-1), 0x28, out _token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var enabled = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(_token, false, ref enabled, Marshal.SizeOf<TokenPrivilege>(), out _previous, out _) || Marshal.GetLastWin32Error() != 0)
                    throw new UnauthorizedAccessException("Windows did not grant the profile-registry privilege required for a complete wipe.");
            }
            catch { _token.Dispose(); throw; }
        }
        public void Dispose() { AdjustTokenPrivileges(_token, false, ref _previous, Marshal.SizeOf<TokenPrivilege>(), out _, out _); _token.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct HiveInformation
    { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)] private static extern SafeFileHandle CreateFile(string file, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HiveInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadKeyW")] private static extern int RegLoadKey(IntPtr root, string key, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegUnLoadKeyW")] private static extern int RegUnLoadKey(IntPtr root, string key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadAppKeyW")] private static extern int RegLoadAppKey(string file, out SafeRegistryHandle key, uint access, uint options, uint reserved);
    [DllImport("advapi32.dll")] private static extern int RegFlushKey(SafeRegistryHandle key);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivilege state, int size, out TokenPrivilege previous, out int returned);
}
