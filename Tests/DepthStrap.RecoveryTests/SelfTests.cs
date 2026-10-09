using System.Reflection.PortableExecutable;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DepthStrap.Recovery;

internal static class SelfTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks++; }
        void Reject(Action operation, string description)
        {
            bool rejected = false;
            try { operation(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ArgumentException) { rejected = true; }
            Check(rejected, description);
        }
        Check(!CookieReset.ClientsRunning(_ => false), "An empty client inventory permits reset prerequisites");
        foreach (string client in new[] { "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxPlayerLauncher", "RobloxStudioLauncherBeta" })
            Check(CookieReset.ClientsRunning(name => name == client), "Player, Studio and their launchers block reset and MAC prerequisites");
        Check(!CookieReset.ClientsRunning(name => name == "UnrelatedApp"), "An unrelated process does not block Roblox reset prerequisites");
        Check(CookieReset.NativePath(@"C:\Fixture\RobloxCookies.dat") == @"\\?\C:\Fixture\RobloxCookies.dat", "Native reset calls use extended drive paths");
        Check(CookieReset.NativePath(@"\\server\share\RobloxCookies.dat") == @"\\?\UNC\server\share\RobloxCookies.dat", "Native reset calls preserve UNC share semantics");
        Reject(() => CookieReset.NativePath("RobloxCookies.dat"), "Native path conversion refuses relative inputs");
        Reject(() => CookieReset.NativePath(@"\\.\C:\RobloxCookies.dat"), "Native path conversion refuses Windows device namespaces");
        foreach (string invalidRoot in new[] { "", "Roblox", @"C:Roblox", @"\Roblox", Path.GetPathRoot(AppContext.BaseDirectory)! })
            Reject(() => new CookieReset(invalidRoot, () => false), "Cookie reset requires an explicit absolute directory below a drive root");
        var backupAcl = new FileSecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        backupAcl.SetOwner(admins);
        backupAcl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        backupAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        WindowsMacBackend.ValidateBackupAccess(backupAcl);
        Check(true, "Inherited administrator access and ordinary read access are supported for backups");
        backupAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
        Reject(() => WindowsMacBackend.ValidateBackupAccess(backupAcl), "An individually writable backup is rejected even beneath a protected directory");
        backupAcl.RemoveAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
        backupAcl.SetOwner(users);
        Reject(() => WindowsMacBackend.ValidateBackupAccess(backupAcl), "An untrusted backup owner cannot change its permissions before restoration");
        backupAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        WindowsMacBackend.ValidateBackupAccess(backupAcl);
        Check(true, "SYSTEM-owned backup files remain supported");
        var approvedAdmin = new SecurityIdentifier("S-1-5-21-1-2-3-1001"); // Synthetic policy input, not a real account.
        backupAcl.AddAccessRule(new FileSystemAccessRule(approvedAdmin, FileSystemRights.FullControl, AccessControlType.Allow));
        Reject(() => WindowsMacBackend.ValidateBackupAccess(backupAcl), "An individual write grant without verified administrator approval is refused");
        WindowsMacBackend.ValidateBackupAccess(backupAcl, approvedAdmin);
        Check(true, "An individual write grant for the current approved administrator remains supported");
        BackupAclFixtureChecks.Run(Check);
        var v4Option = AdapterConnectivity.OutgoingInterfaceOption(System.Net.Sockets.AddressFamily.InterNetwork, 0x010203);
        Check(v4Option.Level == System.Net.Sockets.SocketOptionLevel.IP && v4Option.Value == System.Net.IPAddress.HostToNetworkOrder(0x010203), "IPv4 probe interface uses network byte order");
        var v6Option = AdapterConnectivity.OutgoingInterfaceOption(System.Net.Sockets.AddressFamily.InterNetworkV6, 0x010203);
        Check(v6Option.Level == System.Net.Sockets.SocketOptionLevel.IPv6 && v6Option.Value == 0x010203, "IPv6 probe interface uses host byte order");
        Reject(() => AdapterConnectivity.OutgoingInterfaceOption(System.Net.Sockets.AddressFamily.InterNetwork, 0), "Probe interface zero cannot fall back to the default route");
        Reject(() => AdapterConnectivity.OutgoingInterfaceOption(System.Net.Sockets.AddressFamily.InterNetworkV6, -1), "Negative probe interface indices are refused");
        Reject(() => AdapterConnectivity.OutgoingInterfaceOption(System.Net.Sockets.AddressFamily.InterNetwork, 0x01000000), "IPv4 interface indices fit the documented 24-bit range");
        Reject(() => AdapterConnectivity.OutgoingInterfaceOption(System.Net.Sockets.AddressFamily.Unspecified, 1), "Unknown probe address families cannot select an interface");
        var prompt = new RecoveryPrompt("Fixture only; no deletion runs.", "Reset preview", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        Check(prompt.Background is System.Windows.Media.DrawingBrush { IsFrozen: true }, "Toolkit confirmation uses a shared dimmed arena background");
        Check(prompt.MaxHeight <= System.Windows.SystemParameters.WorkArea.Height, "Toolkit confirmation remains within the work area");
        prompt.Close();
        Check(prompt.Result == System.Windows.MessageBoxResult.No, "Closing toolkit confirmation preserves a negative result");
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reset = new CookieReset(root, () => false);
        File.WriteAllText(reset.Target, "fixture-cookie");
        string other = Path.Combine(root, "settings.json"); File.WriteAllText(other, "preserve");
        Reject(() => reset.Reset(false), "Consent is required");
        Check(File.ReadAllText(reset.Target) == "fixture-cookie", "Unconfirmed reset preserved its fixture");
        Reject(() => new CookieReset(root, () => true).Reset(true), "Running clients block reset");
        using (var token = new CancellationTokenSource())
        { token.Cancel(); Reject(() => reset.Reset(true, token.Token), "Cancelled reset preserves the file"); }
        using (var locked = new FileStream(reset.Target, FileMode.Open, FileAccess.Read, FileShare.None))
        { Reject(() => reset.Reset(true), "Locked cookies are left alone"); }
        int presenceChecks = 0;
        Reject(() => new CookieReset(root, () => ++presenceChecks > 1).Reset(true), "Client starting during reset blocks deletion");
        Check(File.Exists(reset.Target), "A refused handle-based reset did not delete cookies");
        using (var lateCancellation = new CancellationTokenSource())
        {
            int probes = 0;
            var cancelledReset = new CookieReset(root, () => { if (++probes == 2) lateCancellation.Cancel(); return false; });
            Reject(() => cancelledReset.Reset(true, lateCancellation.Token), "Cancellation after final client check preserves cookies");
            Check(File.Exists(reset.Target), "Late cancellation did not delete its fixture");
        }
        Check(reset.Reset(true) == CookieResetResult.Removed && !File.Exists(reset.Target), "The confirmed fixture reset removes exactly the cookie file");
        Check(File.ReadAllText(other) == "preserve", "Cookie reset preserved unrelated files");
        Check(reset.Reset(true) == CookieResetResult.NotPresent, "Missing cookies are an idempotent no-op");
        File.WriteAllText(reset.Target, "read-only synthetic cookie");
        File.SetAttributes(reset.Target, FileAttributes.ReadOnly);
        try
        {
            Check(reset.Reset(true) == CookieResetResult.Removed && !File.Exists(reset.Target),
                "A confirmed cookie reset removes a read-only synthetic cookie without reading its contents");
        }
        finally
        {
            if (File.Exists(reset.Target)) { File.SetAttributes(reset.Target, FileAttributes.Normal); File.Delete(reset.Target); }
        }
        Check(new CookieReset(Path.Combine(root, "missing"), () => false).Reset(true) == CookieResetResult.NotPresent, "A missing Roblox directory is not created by reset");
        Directory.CreateDirectory(reset.Target);
        Reject(() => reset.Reset(true), "A directory named RobloxCookies.dat cannot be deleted by cookie reset");
        Check(Directory.Exists(reset.Target), "The directory-shaped cookie fixture was preserved");
        Directory.Delete(reset.Target);
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        string outsideCookie = Path.Combine(outside, "RobloxCookies.dat"); File.WriteAllText(outsideCookie, "preserve-linked-cookie");
        string link = Path.Combine(root, "redirected");
        JunctionFixture.Create(root, link, outside);
        Reject(() => new CookieReset(link, () => false).Reset(true), "Directory redirects cannot delete another location's cookie");
        Check(File.ReadAllText(outsideCookie) == "preserve-linked-cookie", "Redirected cookie contents were preserved");
        Check(CookieReset.NormalizeFinalPath(@"\\?\C:\fixture") == @"C:\fixture" && CookieReset.NormalizeFinalPath(@"\\?\UNC\server\share\file") == @"\\server\share\file", "Windows final-path prefixes normalize correctly");
        foreach (string value in new[] { "02:11:22:33:44:55", "06-11-22-33-44-55", "0A1122334455" }) Check(AdapterInspection.IsLocalUnicastMac(value), "Local unicast MAC accepted");
        foreach (string? value in new[] { null, "", "00:11:22:33:44:55", "03:11:22:33:44:55", "FFFFFFFFFFFF", "02GG22334455", "021122334455;cmd", "02112233445500", "0:21122334455", "02:11-22:33:44:55", "021122334455\n" }) Check(!AdapterInspection.IsLocalUnicastMac(value), "Invalid/multicast/global MAC rejected");
        Check(AdapterInspection.MaskMac(new byte[] { 2, 17, 34, 51, 68, 85 }) == "**:**:**:33:44:55", "MAC is masked in reports");
        Check(AdapterInspection.MaskMac(Array.Empty<byte>()) == "Unavailable", "Missing MAC is not invented");
        Check(AdapterInspection.Read<string>(() => throw new System.Net.NetworkInformation.NetworkInformationException(), "Unavailable") == "Unavailable", "One unavailable adapter property cannot fail the entire inspection");
        Check(AdapterInspection.Read<long>(() => throw new NotSupportedException(), 0) == 0, "Unsupported link speed is reported without failing other adapter data");
        foreach (string usable in new[] { "192.168.1.20", "10.0.0.2", "2001:db8::20", "fd12::1" })
            Check(AdapterConnectivity.IsUsableSource(System.Net.IPAddress.Parse(usable)), "IPv4/IPv6 unicast sources qualify for adapter-bound connectivity");
        foreach (string unusable in new[] { "0.0.0.0", "127.0.0.1", "169.254.1.2", "224.0.0.1", "255.255.255.255", "::", "::1", "fe80::1", "ff02::1", "::ffff:192.168.1.2" })
            Check(!AdapterConnectivity.IsUsableSource(System.Net.IPAddress.Parse(unusable)), "Unspecified, loopback, link-local and multicast sources cannot prove connectivity");
        var start = HardwareInspection.StartInfo();
        Check(Path.IsPathRooted(start.FileName) && !start.UseShellExecute && start.Verb == "" && start.RedirectStandardOutput && start.ArgumentList.Contains("-NoProfile"), "Inspection uses a fixed non-elevated Windows PowerShell path");
        Check(!HardwareInspection.Script.Contains("Set-") && !HardwareInspection.Script.Contains("Remove-") && HardwareInspection.Script.Contains("MaskedUuid=(Mask"), "Hardware inspection is read-only and masks identifiers before IPC");
        Check(HardwareInspection.IsMasked("****ABCD") && HardwareInspection.IsMasked("Unavailable"), "Masked and unavailable identifiers are accepted");
        Check(!HardwareInspection.IsMasked("12345678") && !HardwareInspection.IsMasked(null) && !HardwareInspection.IsMasked("****12345"), "Unmasked or overlong identifiers are rejected");
        Reject(() => ClientInspection.Inspect(Path.Combine(root, "Other.exe")), "Build inspection rejects other executables");
        string exe = Path.Combine(root, "RobloxPlayerBeta.exe"); File.WriteAllText(exe, "not-a-PE");
        Check(!ClientInspection.Inspect(exe).Complete, "Invalid Roblox binaries are not reported complete");
        RobloxDataResetTests.Run(root, Check);
        RobloxRegistryResetTests.Run(Check);
        RobloxSignInResetTests.Run(root, Check);
        RobloxFullResetTests.Run(root, Check);
        var inventory = WindowsMacBackend.StartInfo("Inventory", Guid.Empty, null);
        Check(!inventory.UseShellExecute && inventory.Verb == "" && inventory.RedirectStandardOutput && inventory.ArgumentList.Contains("-EncodedCommand"), "Driver inventory is non-elevated and uses the embedded script");
        Check(inventory.ArgumentList.Last().Length < 28000 && Encoding.Unicode.GetString(Convert.FromBase64String(inventory.ArgumentList.Last())).Contains("GZipStream"), "Embedded driver commands stay below the Windows process command-line limit");
        Check(Encoding.Unicode.GetString(Convert.FromBase64String(WindowsMacBackend.StartInfo("Set", Guid.NewGuid(), "").ArgumentList.Last())).EndsWith("-RemoveOverride $false"), "An originally empty override is not converted into an absent registry value");
        Reject(() => WindowsMacBackend.StartInfo("Inventory;cmd", Guid.Empty, null), "Driver mode injection is rejected");
        Reject(() => WindowsMacBackend.StartInfo("Set", Guid.NewGuid(), "021122334455;cmd"), "Driver MAC injection is rejected");
        Check(!WindowsMacBackend.DriverScript.Contains("New-NetAdapterAdvancedProperty") && WindowsMacBackend.DriverScript.Contains("NetworkAddress"), "Driver controls require an exposed property instead of inventing one");
        var helper = MacControls.HelperStartInfo(false, Guid.NewGuid(), "021122334455", typeof(Bloxstrap.App).Assembly.Location);
        Check(helper.UseShellExecute && helper.Verb == "runas" && helper.ArgumentList.Count == 3, "MAC mutation requires the explicit administrator helper");
        Guid selectedAdapter = Guid.NewGuid();
        var request = MacHelperRequest.Parse(new[] { "--mac-apply", selectedAdapter.ToString("D"), "02:11:22:33:44:55" });
        Check(request.Adapter == selectedAdapter && request.Address == "021122334455" && !request.Restoring,
            "Helper parsing preserves the selected adapter and normalizes the full requested MAC");
        Check(MacHelperRequest.Parse(request.Arguments()) == request, "Apply helper arguments round-trip without shell command text");
        var restoreRequest = MacHelperRequest.Parse(new[] { "--mac-restore", selectedAdapter.ToString("D") });
        Check(restoreRequest.Restoring && restoreRequest.Address is null && restoreRequest.Arguments().Length == 2,
            "Restore helper cannot receive a replacement address");
        foreach (string[] invalid in new[]
        {
            Array.Empty<string>(), new[] { "--mac-apply" }, new[] { "--unknown", selectedAdapter.ToString("D") },
            new[] { "--mac-restore", selectedAdapter.ToString("D"), "021122334455" },
            new[] { "--mac-apply", Guid.Empty.ToString("D"), "021122334455" },
            new[] { "--mac-apply", selectedAdapter.ToString("N"), "021122334455" },
            new[] { "--mac-apply", selectedAdapter.ToString("D"), "031122334455" },
            new[] { "--mac-apply", selectedAdapter.ToString("D"), "001122334455" },
            new[] { "--mac-apply", selectedAdapter.ToString("D"), "021122334455;cmd" }
        }) Reject(() => MacHelperRequest.Parse(invalid), "Malformed or unsupported helper requests are refused before administrator operations");
        Reject(() => MacControls.HelperStartInfo(false, selectedAdapter, "001122334455"),
            "A non-local MAC cannot reach the administrator handoff");
        string clickStatus = "";
        MacControls.RunClickAsync(false, () => throw new IOException("synthetic-private-path"), message => clickStatus = message).GetAwaiter().GetResult();
        Check(clickStatus.Contains("backups are retained") && !clickStatus.Contains("synthetic-private-path"), "A synchronous prerequisite failure is handled without exposing exception details");
        MacControls.RunClickAsync(true, async () => { await Task.Yield(); throw new System.ComponentModel.Win32Exception(5, "synthetic-private-path"); }, message => clickStatus = message).GetAwaiter().GetResult();
        Check(clickStatus.Contains("restore could not finish") && !clickStatus.Contains("synthetic-private-path"), "An asynchronous restore prerequisite failure does not escape the click handler");
        MacControls.RunClickAsync(false, () => Task.FromException(new ArgumentException("invalid MAC")), message => clickStatus = message).GetAwaiter().GetResult();
        Check(clickStatus == "Enter a complete six-byte MAC address.", "Invalid apply input retains its specific correction message");
        MacControls.RunClickAsync(true, () => Task.FromException(new ArgumentException("synthetic-private-path")), message => clickStatus = message).GetAwaiter().GetResult();
        Check(clickStatus.Contains("restore could not finish"), "Restore errors do not ask for a new MAC address");
        MacControls.RunClickAsync(false, () => Task.FromException(new OperationCanceledException()), message => clickStatus = message).GetAwaiter().GetResult();
        Check(clickStatus.Contains("cancelled") && clickStatus.Contains("backups are retained"), "Cancelled UI work leaves an explicit recovery status");
        int retryRuns = 0;
        MacControls.RunClickAsync(true, () => { retryRuns++; clickStatus = "retry completed"; return Task.CompletedTask; }, message => clickStatus = message).GetAwaiter().GetResult();
        Check(retryRuns == 1 && clickStatus == "retry completed", "A retry after handled failure runs once and retains its own result");
        Console.WriteLine($"PASS: {checks} production recovery checks. No real cookies, adapters or hardware settings were modified.");
    }
}
