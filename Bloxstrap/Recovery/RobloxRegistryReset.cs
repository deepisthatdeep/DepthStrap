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
    private RobloxRegistryReset(string path, Func<bool> clientsRunning) { _path = path; _clientsRunning = clientsRunning; }
    internal static RobloxRegistryReset ForCurrentUser() => new(@"Software\ROBLOX Corporation", CookieReset.ClientsRunning);
    internal static RobloxRegistryReset ForFixture(Guid fixture, Func<bool> clientsRunning) => new(@"Software\DepthStrapRecoveryLabFixtures\" + fixture.ToString("D"), clientsRunning);
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
        return new(_scope, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", stamps)))), count, "HKCU\\" + _path);
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
    private static SafeRegistryHandle? Open(string path, bool writable)
    {
        int result = RegOpenKeyEx(new IntPtr(unchecked((int)0x80000001)), path, 8, writable ? 0xf003fu : 0x20019u, out var key); // OPEN_LINK
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
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr className, IntPtr classLength, IntPtr reserved, IntPtr subKeys, IntPtr maximumSubKey, IntPtr maximumClass,
        IntPtr values, IntPtr maximumValueName, IntPtr maximumValueLength, IntPtr securityLength, out long lastWriteTime);
}
