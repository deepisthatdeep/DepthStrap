using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DepthStrap.Recovery;

internal sealed record RegistryResetPreview(Guid Scope, string Revision, int Values, string Location);

/// <summary>Deletes only Roblox's current-user settings tree; no Windows logs, hardware keys or protocol handlers.</summary>
internal sealed class RobloxRegistryReset
{
    private readonly Guid _scope = Guid.NewGuid();
    private readonly string _path;
    private readonly Func<bool> _clientsRunning;
    private readonly IntPtr _root = new(unchecked((int)0x80000001));
    private readonly uint _view;
    private readonly string _label = "HKCU";
    private RobloxRegistryReset(string path, Func<bool> clientsRunning) { _path = path; _clientsRunning = clientsRunning; }
    internal static RobloxRegistryReset ForCurrentUser() => new(@"Software\ROBLOX Corporation", CookieReset.ClientsRunning);
    internal static RobloxRegistryReset ForFixture(Guid fixture, Func<bool> clientsRunning) => new(@"Software\DepthStrapRecoveryLabFixtures\" + fixture.ToString("D"), clientsRunning);
    internal static RobloxRegistryReset ForOpenRoot(RegistryKey root, string path, string label, Func<bool> running, RegistryView view = RegistryView.Default)
        => new(path, running, root.Handle.DangerousGetHandle(), label, view);
    private RobloxRegistryReset(string path, Func<bool> running, IntPtr root, string label, RegistryView view) : this(path, running)
    { _root = root; _label = label; _view = view == RegistryView.Registry32 ? 0x200u : view == RegistryView.Registry64 ? 0x100u : 0; }
    internal string NativeIdentity()
    {
        using var key = Open(_path, false);
        if (key is null) return "";
        NtQueryKey(key, 3, IntPtr.Zero, 0, out int size);
        if (size is <= 4 or > 65536) throw new IOException("Registry identity is unavailable.");
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryKey(key, 3, buffer, size, out _) != 0) throw new IOException("Registry identity is unavailable.");
            int bytes = Marshal.ReadInt32(buffer);
            if (bytes < 0 || bytes > size - 4 || bytes % 2 != 0) throw new IOException("Invalid registry identity.");
            return Marshal.PtrToStringUni(buffer + 4, bytes / 2)!;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal RegistryResetPreview Preview() => Capture(false);
    internal void ValidatePreview(RegistryResetPreview preview, bool confirmed)
    {
        if (!confirmed || preview.Scope != _scope) throw new InvalidOperationException("Confirm this registry reset's current preview first.");
        if (_clientsRunning()) throw new InvalidOperationException("Close Roblox and Studio before resetting Roblox registry data.");
        if (Capture(false).Revision != preview.Revision) throw new InvalidOperationException("Roblox registry data changed. Review a new reset preview.");
        // Every previewed child must also permit deletion before a combined
        // reset removes files. Access to the vendor root alone is insufficient.
        if (Capture(true).Revision != preview.Revision) throw new InvalidOperationException("Roblox registry data changed. Review a new reset preview.");
    }
    internal void Execute(RegistryResetPreview preview, bool confirmed)
    {
        ValidatePreview(preview, confirmed);
        using var root = Open(_path, true);
        var actual = Capture(false);
        if (actual.Revision != preview.Revision) throw new InvalidOperationException("Roblox registry data changed. Review a new reset preview.");
        if (root is null) return;
        if (_clientsRunning()) throw new InvalidOperationException("A Roblox client started. Registry data was preserved.");
        if (RegDeleteTree(root, null) != 0) throw new IOException("The Roblox registry reset did not complete. Some values may remain.");
        using var key = RegistryKey.FromHandle(root);
        if (key.ValueCount != 0 || key.SubKeyCount != 0) throw new IOException("Roblox registry data was recreated during reset. Review a new preview.");
        if (_clientsRunning()) throw new InvalidOperationException("A Roblox client started. Registry reset is incomplete.");
        // Delete the verified open key itself, rather than reopening a possibly
        // replaced path. No kernel driver is involved in this user-mode call.
        if (NtDeleteKey(root) != 0) throw new IOException("Roblox registry values were cleared, but the root key could not be deleted. Reset is incomplete.");
        key.Dispose();
        using var remaining = Open(_path, false);
        if (remaining is not null) throw new IOException("Roblox registry data was recreated during reset. Review a new preview.");
    }
    private RegistryResetPreview Capture(bool writable)
    {
        int count = 0, nodes = 0; var stamps = new List<string>();
        Visit(_path);
        return new(_scope, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", stamps)))), count, _label + "\\" + _path);
        void Visit(string path)
        {
            if (++nodes > 10000) throw new IOException("Roblox registry data exceeds the preview limit.");
            using var handle = Open(path, writable);
            if (handle is null) return;
            using var key = RegistryKey.FromHandle(handle);
            if (RegQueryInfoKey(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long stamp) != 0)
                throw new IOException("Roblox registry metadata could not be inspected.");
            count += key.ValueCount;
            stamps.Add(path + "|" + stamp + "|" + string.Join("|", key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase)));
            foreach (string child in key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase)) Visit(path + "\\" + child);
        }
    }
    private SafeRegistryHandle? Open(string path, bool writable)
    {
        // Read/enumerate, set values and delete; never request WRITE_DAC or
        // WRITE_OWNER. A deletable key need not permit changing its security.
        int result = RegOpenKeyEx(_root, path, 8, (writable ? 0x3001bu : 0x20019u) | _view, out var key); // OPEN_LINK
        if (result == 2) { key.Dispose(); return null; }
        if (result != 0) { key.Dispose(); throw new IOException("Roblox registry data is inaccessible."); }
        uint length = 0;
        result = RegQueryValueEx(key, "SymbolicLinkValue", IntPtr.Zero, out _, IntPtr.Zero, ref length);
        if (result != 2) { key.Dispose(); throw new IOException("Linked or unexpected Roblox registry data cannot be reset."); }
        return key;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(IntPtr root, string path, uint options, uint access, out SafeRegistryHandle key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, IntPtr data, ref uint length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegDeleteTreeW")]
    private static extern int RegDeleteTree(SafeRegistryHandle key, string? child);
    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle key);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryKey(SafeRegistryHandle key, int informationClass, IntPtr buffer, int length, out int required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr className, IntPtr classLength, IntPtr reserved, IntPtr subKeys, IntPtr maximumSubKey, IntPtr maximumClass,
        IntPtr values, IntPtr maximumValueName, IntPtr maximumValueLength, IntPtr securityLength, out long lastWriteTime);
}
