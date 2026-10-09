using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DepthStrap.Recovery;

internal sealed record ResetPreview(Guid Scope, string Revision, string[] Locations, int Files, long Bytes);
internal sealed record ResetOutcome(bool Complete, int RemovedFiles, int RemovedDirectories, string Message);

/// <summary>Explicit current-user Roblox filesystem reset. Never follows redirects or reads file contents.</summary>
internal sealed class RobloxDataReset
{
    private readonly Guid _scope = Guid.NewGuid();
    private readonly string[] _roots;
    private readonly Func<bool> _clientsRunning;
    private const int MaximumEntries = 50000;
    internal RobloxDataReset(IEnumerable<string> roots, Func<bool> clientsRunning)
    {
        _roots = roots.Select(root =>
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new ArgumentException("Reset locations must be explicit absolute paths.");
            return Path.GetFullPath(root);
        }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        _clientsRunning = clientsRunning;
        foreach (string root in _roots)
        {
            if (root.TrimEnd('\\') == Path.GetPathRoot(root)?.TrimEnd('\\'))
                throw new ArgumentException("A reset cannot target a drive root.");
            if (_roots.Any(other => other != root && root.StartsWith(other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Reset locations cannot overlap.");
        }
    }
    internal static RobloxDataReset ForCurrentUser(string? customDepthStrapExecutable = null)
    {
        var roots = CurrentUserRoots(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath()).ToList();
        if (customDepthStrapExecutable is not null) roots.AddRange(ManagedInstallationRoots(customDepthStrapExecutable));
        string[] normalized = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(normalized.Where(root => !normalized.Any(parent => !root.Equals(parent, StringComparison.OrdinalIgnoreCase)
            && root.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))), CookieReset.ClientsRunning);
    }
    internal static string[] ManagedInstallationRoots(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("DepthStrap.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable)) throw new IOException("Select DepthStrap.exe from the custom installation folder.");
        string folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        if (folder.TrimEnd('\\').Equals(Path.GetPathRoot(folder)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A drive root is not a custom installation folder.");
        return new[] { Path.Combine(folder, "Versions"), Path.Combine(folder, "Downloads") };
    }
    internal RobloxDataReset IncludeManagedInstallation(string executable)
    {
        string[] roots = _roots.Concat(ManagedInstallationRoots(executable)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(roots.Where(root => !roots.Any(parent => !root.Equals(parent, StringComparison.OrdinalIgnoreCase)
            && root.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))), _clientsRunning);
    }
    internal static string[] CurrentUserRoots(string local, string roaming, string profile, string temporary)
    {
        foreach (string basis in new[] { local, roaming, profile, temporary })
            if (!Path.IsPathFullyQualified(basis)) throw new IOException("The current user's data locations are unavailable.");
        var roots = new List<string>
        {
            Path.Combine(local, "Roblox"), Path.Combine(local, "rbx-storage"), Path.Combine(roaming, "Roblox"),
            Path.Combine(profile, "AppData", "LocalLow", "Roblox"), Path.Combine(temporary, "Roblox"),
            Path.Combine(local, "DepthStrap", "Versions"), Path.Combine(local, "DepthStrap", "Downloads")
        };
        string packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (string package in Directory.EnumerateDirectories(packages, "ROBLOXCORPORATION.ROBLOX_*"))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(package), @"\AROBLOXCORPORATION\.ROBLOX_[a-z0-9]{13}\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) roots.Add(package);
        }
        return roots.ToArray();
    }

    internal ResetPreview Preview(CancellationToken token = default)
    {
        using var snapshot = Capture(token);
        return Describe(snapshot);
    }
    internal ResetOutcome Execute(ResetPreview preview, bool confirmed, CancellationToken token = default)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm permanent deletion and sign-out first.");
        if (preview.Scope != _scope) throw new InvalidOperationException("Create a new preview for this reset.");
        if (_clientsRunning()) throw new InvalidOperationException("Close all Roblox Player and Studio clients first.");
        using var snapshot = Capture(token);
        if (Describe(snapshot).Revision != preview.Revision) throw new InvalidOperationException("Roblox data changed after the preview. Review a new preview before deleting.");
        if (_clientsRunning()) throw new InvalidOperationException("A Roblox client started. The reset was cancelled.");
        token.ThrowIfCancellationRequested();
        int files = 0, directories = 0;
        // All entries have verified, retained handles before the first deletion.
        // Child handles close before their parent is deleted. New data can make
        // a directory nonempty; this is reported as incomplete rather than swept.
        foreach (var entry in snapshot.Entries.OrderByDescending(entry => entry.Path.Length))
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (_clientsRunning()) throw new InvalidOperationException("A Roblox client started during reset.");
                token.ThrowIfCancellationRequested();
                entry.Delete();
                if (entry.Directory) directories++; else files++;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                return new(false, files, directories, "Reset incomplete. Some files may already be removed; remaining data was preserved. Close Roblox and review a new preview before retrying.");
            }
        }
        return new(true, files, directories, "The listed Roblox data folders were removed. Sign-in and local settings will reset. Other installations and Windows logs were not changed.");
    }
    private Snapshot Capture(CancellationToken token)
    {
        var snapshot = new Snapshot();
        try
        {
            var pending = new Stack<(string Path, bool Root)>(_roots.Select(root => (root, true)));
            while (pending.TryPop(out var next))
            {
                token.ThrowIfCancellationRequested();
                if (snapshot.Entries.Count >= MaximumEntries) throw new IOException("Reset exceeds the preview entry limit.");
                var entry = Entry.Open(next.Path, next.Root);
                if (entry is null) continue;
                snapshot.Entries.Add(entry);
                if (next.Root && !entry.Directory) throw new IOException("An expected Roblox directory is a file; nothing was removed.");
                if (!entry.Directory) continue;
                foreach (string child in Directory.EnumerateFileSystemEntries(next.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (snapshot.Entries.Count + pending.Count >= MaximumEntries) throw new IOException("Reset exceeds the preview entry limit.");
                    pending.Push((child, false));
                }
            }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }
    private ResetPreview Describe(Snapshot snapshot)
    {
        // Hash metadata, not contents. File IDs catch same-size replacements.
        string metadata = string.Join("\n", snapshot.Entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Stamp));
        return new(_scope, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metadata))), _roots.ToArray(),
            snapshot.Entries.Count(entry => !entry.Directory), snapshot.Entries.Where(entry => !entry.Directory).Sum(entry => entry.Size));
    }
    private sealed class Snapshot : IDisposable
    {
        internal List<Entry> Entries { get; } = new();
        public void Dispose() { foreach (var entry in Entries) entry.Dispose(); }
    }
    private sealed class Entry(SafeFileHandle handle, string path, FileInfoNative info) : IDisposable
    {
        internal string Path => path;
        internal bool Directory => (info.Attributes & 0x10) != 0;
        internal long Size => ((long)info.SizeHigh << 32) | info.SizeLow;
        internal string Stamp => $"{path}|{info.VolumeSerial}|{info.IndexHigh}:{info.IndexLow}|{info.Attributes}|{Size}|{info.WriteHigh}:{info.WriteLow}";
        internal static Entry? Open(string path, bool mayBeAbsent)
        {
            // Try a directory-compatible handle first. File handles deny other
            // reads/writes/deletes; directory handles allow enumeration only.
            string nativePath = CookieReset.NativePath(path);
            uint attributes = GetFileAttributes(nativePath);
            if (attributes == uint.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                if (mayBeAbsent && error is 2 or 3) return null;
                throw new IOException("A reset location could not be inspected.", new Win32Exception(error));
            }
            bool directory = (attributes & 0x10) != 0;
            var file = CreateFile(nativePath, 0x10000 | 0x80, directory ? 3u : 0u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            try
            {
                if (file.IsInvalid) throw new IOException("A Roblox file is locked or inaccessible. Nothing was removed.", new Win32Exception(Marshal.GetLastWin32Error()));
                if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
                    throw new IOException("Linked or changing Roblox folders cannot be reset.");
                var final = new StringBuilder(32768);
                uint count = GetFinalPathNameByHandle(file, final, (uint)final.Capacity, 0);
                if (count == 0 || count >= final.Capacity || !string.Equals(CookieReset.NormalizeFinalPath(final.ToString()), path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A Roblox data path is redirected or could not be verified.");
                return new(file, path, info);
            }
            catch { file.Dispose(); throw; }
        }
        internal void Delete()
        {
            var disposition = new DispositionEx { Flags = 1 | 0x10 }; // DELETE | IGNORE_READONLY_ATTRIBUTE
            if (!SetFileInformationByHandle(handle, 21, ref disposition, 4)) throw new IOException("Windows refused a Roblox data deletion.", new Win32Exception(Marshal.GetLastWin32Error()));
            handle.Dispose();
        }
        public void Dispose() => handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct DispositionEx { public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
    private static extern uint GetFileAttributes(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref DispositionEx info, uint size);
}
