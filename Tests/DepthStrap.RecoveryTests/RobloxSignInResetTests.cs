using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DepthStrap.Recovery;

internal static class RobloxSignInResetTests
{
    internal static void Run(string fixtureRoot, Action<bool, string> check)
    {
        Guid id = Guid.NewGuid(); string fixture = @"Software\DepthStrapRecoveryLabFixtures\" + id.ToString("D");
        string folder = Path.Combine(fixtureRoot, "sign-in-reset"); Directory.CreateDirectory(folder);
        var cookies = new CookieReset(folder, () => false);
        var registry = RobloxRegistryReset.ForFixture(id, () => false);
        var service = new RobloxSignInReset(cookies, registry);
        void Populate()
        {
            File.WriteAllText(cookies.Target, "fixture-cookie");
            using var key = Registry.CurrentUser.CreateSubKey(fixture); key.SetValue("fixture", 1);
        }
        void Reject(Action action, string description)
        { bool refused = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { refused = true; } check(refused, description); }
        void WithDeniedPermission(Action<RegistryKey, RegistrySecurity> operation)
        {
            using var permissions = Registry.CurrentUser.OpenSubKey(fixture, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
            var original = permissions!.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var denied = permissions.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.SetValue, AccessControlType.Deny));
            try { operation(permissions, denied); }
            finally
            {
                var restored = new RegistrySecurity();
                restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                permissions.SetAccessControl(restored);
            }
        }
        try
        {
            Populate(); var preview = service.Preview();
            Reject(() => service.Execute(preview, false), "Combined registry/cookie reset requires confirmation");
            check(File.Exists(cookies.Target) && registry.Preview().Values == 1, "Unconfirmed combined reset preserves both stores");
            using (var locked = new FileStream(cookies.Target, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => service.Execute(preview, true), "Locked cookie blocks combined reset");
            check(registry.Preview().Values == 1, "Cookie preflight failure preserves registry data");
            using (var key = Registry.CurrentUser.OpenSubKey(fixture, true)) key!.SetValue("new", 2);
            Reject(() => service.Execute(preview, true), "Stale combined registry preview prevents cookie deletion");
            check(File.Exists(cookies.Target), "Stale combined preview preserves cookie file");
            var permissionPreview = service.Preview();
            WithDeniedPermission((permissions, denied) =>
            {
                permissions.SetAccessControl(denied);
                Reject(() => service.Execute(permissionPreview, true), "Registry permission denial stops sign-in reset before cookie deletion");
                check(File.Exists(cookies.Target), "Denied registry access preserves the sign-in cookie fixture");
            });
            using (var child = Registry.CurrentUser.CreateSubKey(fixture + @"\DeniedChild")) child.SetValue("fixture", 1);
            var childPreview = service.Preview();
            using (var permissions = Registry.CurrentUser.OpenSubKey(fixture + @"\DeniedChild", RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions))
            {
                var original = permissions!.GetAccessControl().GetSecurityDescriptorBinaryForm();
                var denied = permissions.GetAccessControl();
                using var identity = WindowsIdentity.GetCurrent();
                denied.AddAccessRule(new RegistryAccessRule(identity.User!, RegistryRights.Delete, AccessControlType.Deny));
                try
                {
                    permissions.SetAccessControl(denied);
                    Reject(() => service.Execute(childPreview, true), "Denied registry child blocks sign-in reset before cookie deletion");
                    check(File.Exists(cookies.Target), "Child registry deletion denial preserves the sign-in cookie fixture");
                }
                finally
                {
                    var restored = new RegistrySecurity();
                    restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                    permissions.SetAccessControl(restored);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(fixture + @"\DeniedChild", false);
            var result = service.Execute(service.Preview(), true);
            check(result.Complete && result.Cookies == CookieResetResult.Removed && !File.Exists(cookies.Target), "Combined reset deletes its cookie fixture");
            using (var key = Registry.CurrentUser.OpenSubKey(fixture)) check(key is null, "Combined reset deletes its registry root fixture");
            result = service.Execute(service.Preview(), true);
            check(result.Complete && result.Cookies == CookieResetResult.NotPresent, "Combined reset is idempotent when both stores are absent");
            Populate();
            WithDeniedPermission((permissions, denied) =>
            {
                int cookieProbes = 0;
                var changingCookies = new CookieReset(folder, () =>
                {
                    if (++cookieProbes == 2) permissions.SetAccessControl(denied);
                    return false;
                });
                var changingService = new RobloxSignInReset(changingCookies, registry);
                var changed = changingService.Execute(changingService.Preview(), true);
                check(!changed.Complete && changed.Cookies == CookieResetResult.Removed && !File.Exists(cookies.Target),
                    "Late registry permission loss reports the already-completed cookie deletion accurately");
                check(registry.Preview().Values == 1 && changed.Message.Contains("Registry deletion is incomplete"),
                    "Late registry permission loss preserves its remaining data and explains incomplete reset");
            });
            Populate();
            int probes = 0;
            var racingRegistry = RobloxRegistryReset.ForFixture(id, () => ++probes >= 2);
            var racingService = new RobloxSignInReset(cookies, racingRegistry);
            result = racingService.Execute(racingService.Preview(), true);
            check(!result.Complete && result.Cookies == CookieResetResult.Removed && !File.Exists(cookies.Target), "A client starting between operations reports partial completion accurately");
            using (var key = Registry.CurrentUser.OpenSubKey(fixture)) check(key?.ValueCount == 1, "Late client conflict preserves remaining registry data");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(fixture, false); }
    }
}
