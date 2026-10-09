using Microsoft.Win32;

namespace DepthStrap.Recovery;

internal static class RobloxRegistryResetTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(Action operation, string description)
        { bool rejected = false; try { operation(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { rejected = true; } check(rejected, description); }
        Guid id = Guid.NewGuid(); string fixture = @"Software\DepthStrapRecoveryLabFixtures\" + id.ToString("D");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(fixture))
            { key.SetValue("fixture-setting", "temporary"); using var nested = key.CreateSubKey("nested"); nested.SetValue("fixture", 1); }
            string sibling = fixture + "-preserved";
            using (var key = Registry.CurrentUser.CreateSubKey(sibling)) key.SetValue("keep", "preserve");
            var reset = RobloxRegistryReset.ForFixture(id, () => false); var preview = reset.Preview();
            check(preview.Values == 2, "Registry preview counts values without exporting contents");
            Reject(() => reset.Execute(preview, false), "Registry reset requires confirmation");
            Reject(() => RobloxRegistryReset.ForFixture(id, () => false).Execute(preview, true), "Registry previews cannot cross scopes");
            using (var key = Registry.CurrentUser.OpenSubKey(fixture, true)) key!.SetValue("new-setting", 1);
            Reject(() => reset.Execute(preview, true), "A changed registry preview is refused before deletion");
            var running = RobloxRegistryReset.ForFixture(id, () => true);
            Reject(() => running.Execute(running.Preview(), true), "Running clients block Roblox registry reset");
            reset.Execute(reset.Preview(), true);
            check(reset.Preview().Values == 0, "Confirmed registry reset removes all fixture values and subkeys");
            using (var removed = Registry.CurrentUser.OpenSubKey(fixture)) check(removed is null, "Registry reset deletes the vendor root key itself");
            using (var preserved = Registry.CurrentUser.OpenSubKey(sibling)) check((string?)preserved?.GetValue("keep") == "preserve", "Registry reset preserves sibling keys");
            reset.Execute(reset.Preview(), true);
            check(reset.Preview().Values == 0, "Registry reset remains idempotent");
            using (var key = Registry.CurrentUser.CreateSubKey(fixture)) key.SetValue("SymbolicLinkValue", "unexpected");
            Reject(() => reset.Preview(), "Link-shaped registry data is rejected");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(fixture, false); Registry.CurrentUser.DeleteSubKeyTree(fixture + "-preserved", false); }
    }
}
