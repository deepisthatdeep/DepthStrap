using System.ComponentModel;

namespace DepthStrap.Recovery;

internal static class OfflineHiveLoadingTests
{
    internal static void Run(Action<bool, string> check)
    {
        int profiles = 0, applications = 0;
        string result = OfflineHiveLoading.Open<string>(true, () => null, () => { profiles++; return "profile"; }, () => { applications++; throw new Win32Exception(5); }, "synthetic mixed-ACL user hive");
        check(result == "profile" && profiles == 1 && applications == 0, "Elevated cleanup uses the profile loader without requiring a mixed-ACL hive to satisfy application-hive restrictions");
        result = OfflineHiveLoading.Open<string>(true, () => null, () => throw new Win32Exception(87), () => "application", "synthetic fallback");
        check(result == "application", "A failed profile loader can fall back to an accessible application hive");
        result = OfflineHiveLoading.Open<string>(false, () => null, () => throw new UnauthorizedAccessException(), () => "synthetic", "non-elevated native fixture");
        check(result == "synthetic", "Disposable application-hive tests do not require or request administrator privileges");
        bool mounted = false; int pauses = 0; profiles = 0; applications = 0;
        result = OfflineHiveLoading.Open<string>(true, () => mounted ? "live" : null,
            () => { profiles++; throw new Win32Exception(32); }, () => { applications++; throw new Win32Exception(33); }, "synthetic mount race",
            _ => { pauses++; mounted = true; });
        check(result == "live" && profiles == 1 && applications == 1 && pauses == 1, "A profile mounted by Windows during loading is reopened instead of falsely reporting an offline-hive failure");
        string error = ""; pauses = 0;
        try { OfflineHiveLoading.Open<string>(true, () => null, () => throw new Win32Exception(87), () => throw new Win32Exception(5), "SYNTHETIC NTUSER.DAT", _ => pauses++); }
        catch (IOException ex) { error = ex.Message; }
        check(error.Contains("SYNTHETIC NTUSER.DAT") && error.Contains("profile 87") && error.Contains("application 5") && error.Contains("No wipe was started") && pauses == 0,
            "Permanent loader failures identify the affected hive and both Windows status codes without pretending the wipe succeeded");
        profiles = applications = pauses = 0; error = "";
        try { OfflineHiveLoading.Open<string>(true, () => null, () => { profiles++; throw new Win32Exception(32); }, () => { applications++; throw new Win32Exception(170); }, "busy fixture", _ => pauses++); }
        catch (IOException ex) { error = ex.Message; }
        check(error.Length > 0 && profiles == 3 && applications == 3 && pauses == 2, "Locked hives receive a bounded retry budget and never hang the wipe UI indefinitely");
        applications = 0; bool rejected = false;
        try { OfflineHiveLoading.Open<string>(true, () => null, () => throw new IOException("identity changed"), () => { applications++; return "wrong hive"; }, "redirect fixture"); }
        catch (IOException ex) { rejected = ex.Message == "identity changed"; }
        check(rejected && applications == 0, "A path/file identity failure aborts immediately rather than trying another loader against changed data");
    }
}
