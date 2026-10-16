using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DepthStrap.Recovery;

internal static class StoreServicingFixture
{
    internal static void Run(Action<bool, string> check)
    {
        const string main = "ROBLOXCORPORATION.ROBLOX_1.2.3.4_x64__55nm5eh3cm0pr";
        const string bundle = "ROBLOXCORPORATION.ROBLOX_1.2.3.4_neutral_~_55nm5eh3cm0pr";
        const string provisioned = "ROBLOXCORPORATION.ROBLOX_1.2.3.4_neutral_~_55nm5eh3cm0pr";
        const string mocks = """
            $ErrorActionPreference='Stop'
            $global:FixturePackages=@(
                [pscustomobject]@{Name='ROBLOXCORPORATION.ROBLOX';PublisherId='55nm5eh3cm0pr';PackageFullName='ROBLOXCORPORATION.ROBLOX_1.2.3.4_x64__55nm5eh3cm0pr'},
                [pscustomobject]@{Name='ROBLOXCORPORATION.ROBLOX';PublisherId='55nm5eh3cm0pr';PackageFullName='ROBLOXCORPORATION.ROBLOX_1.2.3.4_neutral_~_55nm5eh3cm0pr'},
                [pscustomobject]@{Name='UnrelatedApp';PublisherId='other';PackageFullName='OtherApp'})
            $global:FixtureProvisioned=@([pscustomobject]@{DisplayName='ROBLOXCORPORATION.ROBLOX';PackageName='ROBLOXCORPORATION.ROBLOX_1.2.3.4_neutral_~_55nm5eh3cm0pr'})
            $global:FixtureCalls=@()
            function Get-AppxPackage {param([switch]$AllUsers,[string]$Name,[string[]]$PackageTypeFilter) if (!$AllUsers) {throw 'Missing AllUsers'}; return $global:FixturePackages}
            function Get-AppxProvisionedPackage {param([switch]$Online) if (!$Online) {throw 'Missing Online'}; return $global:FixtureProvisioned}
            function Remove-AppxPackage {param([string]$Package,[switch]$AllUsers,[bool]$Confirm) if (!$AllUsers -or $Confirm) {throw 'Unexpected removal flags'}; $global:FixtureCalls+=('Installed:'+ $Package); $global:FixturePackages=@($global:FixturePackages | Where-Object {$_.PackageFullName -ne $Package})}
            function Remove-AppxProvisionedPackage {param([switch]$Online,[string]$PackageName,[switch]$AllUsers) if (!$Online -or !$AllUsers) {throw 'Unexpected provisioned flags'}; $global:FixtureCalls+=('Provisioned:'+$PackageName); $global:FixtureProvisioned=@($global:FixtureProvisioned | Where-Object {$_.PackageName -ne $PackageName})}
            """;
        (int Code, string Output, string Errors) Run(RobloxStoreInventory? approved, string extra = "")
        {
            var start = RobloxStorePackages.StartInfo(approved);
            string productionInvocation = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList.Last()));
            string fixture = mocks + "\n" + extra + "\n" + productionInvocation + "\n@{Calls=@($global:FixtureCalls)} | ConvertTo-Json -Compress\n";
            start.ArgumentList[start.ArgumentList.Count - 1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(fixture));
            using var worker = Process.Start(start) ?? throw new IOException("Mock PowerShell could not start.");
            var output = worker.StandardOutput.ReadToEndAsync(); var error = worker.StandardError.ReadToEndAsync();
            if (!worker.WaitForExit(30000)) { worker.Kill(); throw new IOException("Mock servicing test timed out."); }
            return (worker.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        var inventory = Run(null);
        check(inventory.Code == 0, "The exact production Store script executes against mocked Windows cmdlets: " + inventory.Errors);
        using (var parsed = JsonDocument.Parse(inventory.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]))
            check(parsed.RootElement.GetProperty("Installed").GetArrayLength() == 2, "Package discovery excludes an unrelated app in the mocked inventory");
        var removed = Run(new([main, bundle], [provisioned]));
        check(removed.Code == 0, "The production script completes bundle/main/provisioned removal against mocks: " + removed.Errors);
        string[] lines = removed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var parsed = JsonDocument.Parse(lines[0])) check(parsed.RootElement.GetProperty("Installed").GetArrayLength() == 0 && parsed.RootElement.GetProperty("Provisioned").GetArrayLength() == 0, "Post-servicing inventory verifies no target packages remain");
        using (var parsed = JsonDocument.Parse(lines[^1]))
        {
            var calls = parsed.RootElement.GetProperty("Calls").EnumerateArray().Select(item => item.GetString()).ToArray();
            check(calls.SequenceEqual(new[] { "Installed:" + bundle, "Installed:" + main, "Provisioned:" + provisioned }), "Bundle removal precedes main-package removal and all-user provisioning removal");
        }
        var stale = Run(new([], [])); check(stale.Code != 0, "The production script refuses a changed package inventory before removal");
        var empty = Run(new([], []), "$global:FixturePackages=@();$global:FixtureProvisioned=@()");
        check(empty.Code == 0, "An already-empty Store inventory is idempotent under Windows PowerShell");
    }
}
