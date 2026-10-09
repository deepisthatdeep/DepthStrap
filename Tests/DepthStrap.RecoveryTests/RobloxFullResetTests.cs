using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DepthStrap.Recovery;

internal static class RobloxFullResetTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        Guid id = Guid.NewGuid();
        string keyPath = @"Software\DepthStrapRecoveryLabFixtures\" + id.ToString("D");
        string folder = Path.Combine(root, "combined-full-reset");
        var data = new RobloxDataReset(new[] { folder }, () => false);
        var registry = RobloxRegistryReset.ForFixture(id, () => false);
        var service = new RobloxFullReset(data, registry);
        void Populate()
        {
            Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "RobloxCookies.dat"), "synthetic-cookie");
            using var key = Registry.CurrentUser.CreateSubKey(keyPath); key.SetValue("synthetic", 1);
        }
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { rejected = true; }
            check(rejected, description);
        }
        try
        {
            Populate(); var preview = service.Preview();
            Reject(() => service.Execute(preview, false), "Combined folder/registry reset requires confirmation");
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)) key!.SetValue("changed", 2);
            Reject(() => service.Execute(preview, true), "Stale registry preview stops full reset before deleting files");
            check(File.Exists(Path.Combine(folder, "RobloxCookies.dat")), "Stale registry preview preserves cookies and folders");
            preview = service.Preview();
            using (var held = new FileStream(Path.Combine(folder, "RobloxCookies.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => service.Execute(preview, true), "Locked data blocks combined reset before registry deletion");
            check(registry.Preview().Values == 2, "Folder preflight failure preserves registry data");
            using (var permissions = Registry.CurrentUser.OpenSubKey(keyPath, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
            {
                var originalAcl = permissions!.GetAccessControl();
                var denied = permissions.GetAccessControl();
                using var identity = WindowsIdentity.GetCurrent();
                denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.SetValue, AccessControlType.Deny));
                permissions.SetAccessControl(denied);
                try
                {
                    Reject(() => service.Execute(preview, true), "Registry write-access denial stops full reset before deleting files");
                    check(File.Exists(Path.Combine(folder, "RobloxCookies.dat")), "Registry access failure preserves the cookie fixture");
                }
                finally
                {
                    var restored = new RegistrySecurity();
                    restored.SetSecurityDescriptorBinaryForm(originalAcl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    permissions.SetAccessControl(restored);
                }
            }
            using (var child = Registry.CurrentUser.CreateSubKey(keyPath + @"\DeniedChild")) child.SetValue("fixture", 1);
            var childPreview = service.Preview();
            using (var permissions = Registry.CurrentUser.OpenSubKey(keyPath + @"\DeniedChild", RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
            {
                var original = permissions!.GetAccessControl().GetSecurityDescriptorBinaryForm();
                var denied = permissions.GetAccessControl();
                using var identity = WindowsIdentity.GetCurrent();
                denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.Delete, AccessControlType.Deny));
                try
                {
                    permissions.SetAccessControl(denied);
                    Reject(() => service.Execute(childPreview, true), "Denied child registry deletion is discovered before full-reset file deletion");
                    check(File.Exists(Path.Combine(folder, "RobloxCookies.dat")), "Denied registry child preserves full-reset cookies and folders");
                }
                finally
                {
                    var restored = new RegistrySecurity();
                    restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                    permissions.SetAccessControl(restored);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(keyPath + @"\DeniedChild", false);
            var result = service.Execute(service.Preview(), true);
            check(result.Complete && !Directory.Exists(folder) && registry.Preview().Values == 0, "Combined full reset removes both fixture stores");
            check(service.Execute(service.Preview(), true).Complete, "Combined full reset is idempotent");
            Populate(); int probes = 0;
            var race = new RobloxFullReset(data, RobloxRegistryReset.ForFixture(id, () => ++probes >= 2));
            result = race.Execute(race.Preview(), true);
            check(!result.Complete && result.Data.Complete && !Directory.Exists(folder), "A client appearing after folder deletion is reported as incomplete");
            check(registry.Preview().Values == 1, "Late client conflict preserves the remaining registry store");
            Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "a"), "first"); File.WriteAllText(Path.Combine(folder, "b"), "second");
            probes = 0;
            var partial = new RobloxFullReset(new RobloxDataReset(new[] { folder }, () => ++probes >= 4), registry);
            result = partial.Execute(partial.Preview(), true);
            check(!result.Complete && result.Data.RemovedFiles == 1 && registry.Preview().Values == 1,
                "Interruption after the first deletion reports partial removal and retains registry data");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }
}
