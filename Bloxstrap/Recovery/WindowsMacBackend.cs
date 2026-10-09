using System.Net.NetworkInformation;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using DepthStrap.Toolkit;

namespace DepthStrap.Recovery;

/// <summary>Windows adapter backend. Must run with explicitly approved elevation before any write.</summary>
internal sealed class WindowsMacBackend : IMacChangeBackend
{
    private static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DepthStrapRecovery", "MAC");
    private FileStream? _lock;
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    public ValueTask<IAsyncDisposable> LockAsync(Guid adapter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required for MAC changes.");
        if (adapter == Guid.Empty || _lock is not null) throw new IOException("Invalid or busy MAC transaction.");
        EnsureProtectedStore();
        var held = new FileStream(Path.Combine(Store, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _lock = held;
        return ValueTask.FromResult<IAsyncDisposable>(new Lease(this, held));
    }
    public async Task<MacAdapter?> ReadAsync(Guid adapter, CancellationToken token)
    {
        string result = await RunAsync("Read", adapter, null, token);
        return result.Trim() == "null" ? null : JsonSerializer.Deserialize<MacAdapter>(result) ?? throw new IOException("Adapter inspection returned no data.");
    }
    public Task<MacBackup?> LoadBackupAsync(Guid adapter, CancellationToken token)
    {
        AssertLock(); token.ThrowIfCancellationRequested();
        string path = BackupPath(adapter);
        if (!File.Exists(path)) return Task.FromResult<MacBackup?>(null);
        AssertUnlinked(path);
        using var identity = WindowsIdentity.GetCurrent();
        ValidateBackupAccess(new FileInfo(path).GetAccessControl(), identity.User);
        if (new FileInfo(path).Length > 4096) throw new IOException("Oversized MAC backup.");
        return Task.FromResult<MacBackup?>(JsonSerializer.Deserialize<MacBackup>(File.ReadAllText(path)) ?? throw new IOException("Invalid MAC backup."));
    }
    public Task SaveBackupAsync(MacBackup backup, CancellationToken token)
    {
        AssertLock(); token.ThrowIfCancellationRequested();
        string target = BackupPath(backup.Adapter);
        if (File.Exists(target)) throw new IOException("An existing backup cannot be replaced.");
        string temporary = Path.Combine(Store, Guid.NewGuid().ToString("N") + ".pending");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, backup); stream.Flush(true);
            }
            File.Move(temporary, target, false);
        }
        finally
        {
            // Never sweep older pending records: only this attempt's newly
            // created file may be removed. Preserve the original save failure.
            if (created && File.Exists(temporary))
            {
                try { AssertUnlinked(temporary); File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return Task.CompletedTask;
    }
    public Task DeleteBackupAsync(Guid adapter, CancellationToken token)
    { AssertLock(); token.ThrowIfCancellationRequested(); string path = BackupPath(adapter); AssertUnlinked(path); File.Delete(path); return Task.CompletedTask; }
    public async Task SetOverrideAsync(Guid adapter, string? address, CancellationToken token)
    { AssertLock(); await RunAsync("Set", adapter, address, CancellationToken.None); }
    public async Task<ConnectionState> CheckConnectivityAsync(Guid adapter, CancellationToken token)
    {
        // Do not mistake another NIC's working route for connectivity on the
        // changed adapter. Probe sockets and ICMP are bound to its source IP.
        for (int pass = 0; pass < 3; pass++)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => Guid.TryParse(item.Id, out var id) && id == adapter);
            if (nic is null) return ConnectionState.Unknown;
            var properties = nic.GetIPProperties();
            var sources = properties.UnicastAddresses.Select(item => item.Address).Where(AdapterConnectivity.IsUsableSource).Distinct().ToArray();
            // Parallel bounded probes avoid penalizing dual-stack adapters when
            // one address family is unavailable. Only this adapter's addresses are used.
            var probes = sources.Select(source =>
            {
                int index;
                try { index = source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? properties.GetIPv4Properties()?.Index ?? 0 : properties.GetIPv6Properties()?.Index ?? 0; }
                catch (NetworkInformationException) { return Task.FromResult(false); }
                return AdapterConnectivity.ProbeAsync(source, index, token);
            }).ToList();
            while (probes.Count > 0)
            {
                Task<bool> finished = await Task.WhenAny(probes); probes.Remove(finished);
                if (await finished) { await Task.WhenAll(probes); return ConnectionState.Online; }
            }
            if (pass < 2) await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
        return ConnectionState.Unknown; // Blocked probes do not prove loss of Internet access.
    }
    private static string BackupPath(Guid id) => Path.Combine(Store, id.ToString("D") + ".json");
    internal static void ValidateBackupAccess(FileSecurity acl, SecurityIdentifier? approvedOwner = null)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || (!owner.Equals(admins) && !owner.Equals(system) && !owner.Equals(approvedOwner)))
            throw new IOException("The MAC backup file is not owned by a trusted administrator.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(approvedOwner))
                throw new IOException("The MAC backup file is writable by an untrusted identity.");
    }
    private void AssertLock() { if (_lock is null || !IsAdministrator()) throw new UnauthorizedAccessException("A protected MAC transaction is required."); }
    private static void AssertUnlinked(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("MAC storage cannot use redirected paths."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private static void EnsureProtectedStore()
    {
        string parent = Path.GetDirectoryName(Store)!;
        AssertUnlinked(parent);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        DirectorySecurity Security()
        {
            var acl = new DirectorySecurity(); acl.SetOwner(admins); acl.SetAccessRuleProtection(true, false);
            foreach (var identity in new[] { admins, system }) acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            return acl;
        }
        foreach (string path in new[] { parent, Store })
        {
            AssertUnlinked(path);
            if (!Directory.Exists(path)) new DirectoryInfo(path).Create(Security());
            var acl = new DirectoryInfo(path).GetAccessControl();
            if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier))?.Equals(admins) != true) throw new IOException("Existing MAC backup storage is not administrator-owned and protected.");
            const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 && !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system))
                    throw new IOException("MAC backups are writable by an untrusted identity.");
        }
    }
    private sealed class Lease(WindowsMacBackend owner, FileStream held) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // A stale lease must never clear or close a newer transaction.
                if (ReferenceEquals(owner._lock, held)) owner._lock = null;
                held.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }

    internal static string DriverScript { get; } = LoadDriverScript();
    private static string LoadDriverScript()
    {
        using var resource = typeof(WindowsMacBackend).Assembly.GetManifestResourceStream("DepthStrap.Recovery.AdapterDriver.ps1")
            ?? throw new IOException("The embedded adapter backend is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        string script = reader.ReadToEnd();
        if (script.Length is 0 or > 65536) throw new IOException("The adapter backend exceeds the supported resource size.");
        return script;
    }
    internal static ProcessStartInfo StartInfo(string mode, Guid adapter, string? address)
    {
        if (mode is not ("Read" or "Set" or "Inventory")) throw new ArgumentException("Invalid MAC operation.");
        address = MacChange.NormalizeOverride(address);
        if (mode != "Inventory" && adapter == Guid.Empty) throw new ArgumentException("Select one adapter.");
        using var compressed = new MemoryStream();
        using (var zip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zip.Write(Encoding.UTF8.GetBytes(DriverScript));
        // Keep the owned script inside the encoded command without approaching
        // Windows' process command-line limit or reading an elevated disk script.
        string invocation = "$ProgressPreference='SilentlyContinue';$payload=[IO.MemoryStream]::new([Convert]::FromBase64String('" + Convert.ToBase64String(compressed.ToArray())
            + "'));$zip=[IO.Compression.GZipStream]::new($payload,[IO.Compression.CompressionMode]::Decompress);$reader=[IO.StreamReader]::new($zip,[Text.Encoding]::UTF8);"
            + "$script=$reader.ReadToEnd();$reader.Dispose();$zip.Dispose();$payload.Dispose();& ([scriptblock]::Create($script)) -Mode '" + mode + "' -Adapter '" + adapter.ToString("D") + "' -Address '" + (address ?? "")
            + "' -RemoveOverride " + (address is null ? "$true" : "$false");
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation));
        if (encoded.Length > 28000) throw new IOException("The adapter command exceeds the Windows command-line budget.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }) start.ArgumentList.Add(arg);
        return start;
    }
    internal static async Task<string> RunAsync(string mode, Guid adapter, string? address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var worker = Process.Start(StartInfo(mode, adapter, address)) ?? throw new IOException("Adapter inspection could not start.");
        var output = worker.StandardOutput.ReadToEndAsync(); var errors = worker.StandardError.ReadToEndAsync();
        if (mode == "Set") await worker.WaitForExitAsync(); // Never abandon a mutation halfway through.
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await worker.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!worker.HasExited) worker.Kill(); await worker.WaitForExitAsync(); await Task.WhenAll(output, errors); throw new IOException("Read-only adapter inspection timed out."); }
        }
        await errors;
        if (worker.ExitCode != 0) throw new IOException("Windows could not complete the requested adapter operation.");
        return await output;
    }
}
