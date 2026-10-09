namespace Bloxstrap.Utility;

// The production startup policy and orchestration run unchanged in isolated fixtures.
internal sealed record AppUpdateRuntime(
    HttpClient Client,
    Settings Settings,
    LaunchSettings Launch,
    string CurrentVersion,
    string UpdateDirectory,
    bool StorageAvailable,
    Func<bool> ClientsRunning,
    Func<bool> SaveState,
    Func<IDisposable?> AcquireHandoff,
    Func<ProcessStartInfo, bool> StartUpdater);
