namespace DepthStrap.Recovery;

internal sealed record CompleteUninstallPreview(ResetPreview Files, RobloxWipeFiles Catalog, RobloxWipeRegistryItem[] Registry,
    RobloxStoreInventory Store, string[] Profiles, string? PreservedExecutable, string[] ServicedStoreLocations, BrowserCookiePreview BrowserCookies);

/// <summary>A reviewed, all-user uninstall plan. Any unexplored or remaining data is reported as incomplete.</summary>
internal sealed class RobloxCompleteUninstall : IDisposable
{
    private readonly RobloxWipeRegistryScope? _scope;
    private readonly Func<RobloxWipeProfile[]> _profiles;
    private readonly Func<List<RobloxWipeRegistryItem>> _registry;
    private readonly Func<RobloxWipeFiles> _catalog;
    private readonly Func<bool> _running;
    private readonly IRobloxStorePackages _store;
    private readonly string? _preserve;
    private readonly BrowserCookieReset _browserCookies;
    private RobloxDataReset? _files;
    private CompleteUninstallPreview? _approved;
    internal IProgress<string>? Progress { get; set; }
    internal RobloxCompleteUninstall(Func<RobloxWipeProfile[]> profiles, Func<List<RobloxWipeRegistryItem>> registry,
        Func<RobloxWipeFiles> catalog, IRobloxStorePackages store, Func<bool> running, string? preserve, RobloxWipeRegistryScope? scope = null, BrowserCookieReset? browserCookies = null)
    { _profiles = profiles; _registry = registry; _catalog = catalog; _store = store; _running = running; _preserve = preserve; _scope = scope;
        _browserCookies = browserCookies ?? new BrowserCookieReset(() => [], () => false); }
    internal static RobloxCompleteUninstall ForWindows(string installedExecutable)
    {
        if (!WindowsMacBackend.IsAdministrator()) throw new UnauthorizedAccessException("Administrator approval is required.");
        RobloxWipeCatalog.InstallationDirectory(installedExecutable);
        if (RobloxWipeCatalog.ClientsRunning() || RobloxProcessCleanup.Inventory().Length != 0)
            throw new InvalidOperationException("Close remaining Roblox, Studio, DepthStrap and supported browser processes using the close-processes button, then confirm Complete wipe again.");
        var profiles = RobloxWipeRegistryScope.Profiles();
        var scope = RobloxWipeRegistryScope.Open(profiles);
        try
        {
            string[] shared = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Where(path => !string.IsNullOrEmpty(path))
                .SelectMany(path => new[] { Path.Combine(path, "Roblox"), Path.Combine(path, "DepthStrap") })
                .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DepthStrapRecovery")).ToArray();
            long checkedAt = 0; bool running = false;
            bool ClientsRunning()
            {
                long now = Stopwatch.GetTimestamp();
                if (checkedAt == 0 || Stopwatch.GetElapsedTime(checkedAt, now) >= TimeSpan.FromMilliseconds(250))
                { running = RobloxWipeCatalog.ClientsRunning() || RobloxProcessCleanup.Inventory().Length != 0; checkedAt = now; }
                return running;
            }
            return new(RobloxWipeRegistryScope.Profiles, () => RobloxWipeCatalog.RegistryItems(scope.Roots),
                () => {
                    var skipped = new List<string>();
                    var extra = RobloxWipeCatalog.RegisteredInstallations(scope.Roots).Concat(
                        new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }
                        .SelectMany(folder => RobloxWipeCatalog.FindShortcuts(folder, CancellationToken.None, skipped))).ToArray();
                    var catalog = RobloxWipeCatalog.Files(profiles, shared, installedExecutable, true, CancellationToken.None, extra);
                    return catalog with { Skipped = catalog.Skipped.Concat(skipped).Distinct().Order().ToArray() };
                },
                new RobloxStorePackages(), ClientsRunning,
                File.Exists(installedExecutable) ? Path.GetFullPath(installedExecutable) : null, scope, BrowserCookieReset.ForProfiles(profiles));
        }
        catch { scope.Dispose(); throw; }
    }
    internal async Task<CompleteUninstallPreview> PreviewAsync()
    {
        _approved = null;
        if (_running()) throw new InvalidOperationException("Close remaining Roblox, Studio, DepthStrap and supported browser processes, then confirm Complete wipe again.");
        var profiles = _profiles();
        Progress?.Report("Scanning supported browser cookies and Firefox recovery records…");
        var browserCookies = _browserCookies.Preview();
        Progress?.Report("Finding Roblox, Studio and DepthStrap data across Windows profiles…");
        var catalog = _catalog();
        Progress?.Report("Checking Windows Store packages…");
        var store = await _store.InventoryAsync().ConfigureAwait(false);
        // Store settings hives may remain locked after the client exits. Let
        // Windows remove that package data through servicing rather than opening
        // every package file for deletion before its uninstall can even start.
        string[] storeLocations = store.Installed.Length + store.Provisioned.Length == 0 ? [] : profiles
            .Select(profile => Path.Combine(profile.Directory, "AppData", "Local", "Packages", "ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr"))
            .Where(path => catalog.Locations.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
        _files = new(catalog.Locations.Where(path => !storeLocations.Contains(path, StringComparer.OrdinalIgnoreCase)), _running, allowRootFiles: true, preservedExecutable: _preserve);
        Progress?.Report("Checking file identities and deletion permissions…");
        var data = _files.Preview();
        Progress?.Report("Checking Roblox and DepthStrap registry entries…");
        var registry = _registry().ToArray();
        _approved = new(data, catalog, registry, store, profiles.Select(profile => profile.Sid + "|" + profile.Directory).Order(StringComparer.Ordinal).ToArray(), _preserve, storeLocations, browserCookies);
        return _approved;
    }
    internal async Task<ResetOutcome> ExecuteAsync(CompleteUninstallPreview preview, bool confirmed)
    {
        if (!confirmed || !ReferenceEquals(preview, _approved) || _files is null) throw new InvalidOperationException("Confirm Complete wipe first.");
        Progress?.Report("Rechecking the approved files, registry, profiles and browser cookies…");
        if (_running()) throw new InvalidOperationException("A Roblox, Studio, DepthStrap or supported browser process is still running. Nothing was removed.");
        if (!_profiles().Select(profile => profile.Sid + "|" + profile.Directory).Order(StringComparer.Ordinal).SequenceEqual(preview.Profiles))
            throw new IOException("The Windows profile inventory changed. No deletion started; confirm and retry Complete wipe.");
        var currentCatalog = _catalog();
        if (!currentCatalog.Locations.SequenceEqual(preview.Catalog.Locations, StringComparer.OrdinalIgnoreCase)
            || !currentCatalog.Projects.Order().SequenceEqual(preview.Catalog.Projects.Order())
            || !currentCatalog.Skipped.Order().SequenceEqual(preview.Catalog.Skipped.Order())) throw new IOException("The uninstall scope changed. No deletion started; confirm and retry Complete wipe.");
        var currentRegistry = _registry();
        if (!currentRegistry.Select(item => item.Identity).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(preview.Registry.Select(item => item.Identity).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Roblox/DepthStrap registrations changed. No deletion started; confirm and retry Complete wipe.");
        foreach (var item in preview.Registry) item.Service.ValidatePreview(item.Preview, true);
        if (!RobloxStorePackages.Same(await _store.InventoryAsync().ConfigureAwait(false), preview.Store)) throw new IOException("The Store package inventory changed. No deletion started; confirm and retry Complete wipe.");
        using var browserCookies = _browserCookies.Prepare(preview.BrowserCookies, true);
        _approved = null;
        Progress?.Report("Removing Roblox, Studio and DepthStrap files…");
        var removed = _files.Execute(preview.Files, true);
        if (!removed.Complete) return removed;
        try
        {
            Progress?.Report("Removing Roblox and DepthStrap registry entries…");
            foreach (var item in preview.Registry) item.Service.Execute(item.Preview, true);
            // The package inventory is revalidated inside the embedded servicing script.
            Progress?.Report("Uninstalling Roblox Windows Store packages…");
            await _store.RemoveAsync(preview.Store).ConfigureAwait(false);
            Progress?.Report("Removing Roblox browser and recovery cookies…");
            long removedBrowserCookies = browserCookies.Delete();
            Progress?.Report("Verifying removal of application data, registry entries and browser cookies…");
            bool registryRemaining = _registry().Count != 0;
            var remainingCatalog = _catalog();
            var verificationRoots = remainingCatalog.Locations.Concat(preview.Catalog.Locations).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            verificationRoots = verificationRoots.Where(path => !verificationRoots.Any(parent => !path.Equals(parent, StringComparison.OrdinalIgnoreCase)
                && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
            bool foldersRemain = !new RobloxDataReset(verificationRoots, _running, true, _preserve).HasOnlyPreservedExecutable();
            _browserCookies.VerifyClean(preview.BrowserCookies);
            string[] unresolved = currentCatalog.Skipped.Concat(remainingCatalog.Skipped).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (registryRemaining || foldersRemain || unresolved.Length != 0)
                return removed with { Complete = false, Message = "Uninstall incomplete: remaining registrations, files or skipped locations require review. No claim of a complete wipe was made.\n" + string.Join("\n", unresolved) };
            _scope?.Dispose();
            if (_scope?.UnloadFailures.Count > 0) return removed with { Complete = false, Message = string.Join("\n", _scope.UnloadFailures) };
            return removed with { Message = "Roblox Player, Studio and DepthStrap were uninstalled from the identified Windows profiles and shared locations. Their identified local app data and Studio projects were removed."
                + (_preserve is null ? " No original installed executable existed to preserve." : "\nOnly this original executable was retained, with its parent directories:\n" + _preserve)
                + $"\nRemoved {removedBrowserCookies:N0} Roblox browser cookies from the identified supported profiles. Unrelated website cookies were preserved."
                + "\nUnlisted copies/custom browser locations, Windows logs, external backups and cloud data are outside this reset. DepthStrap will require setup when opened again." };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return removed with { Complete = false, Message = "Wipe incomplete. Files have already been removed; a registry, package, browser-cookie or verification step failed. Resolve the reported issue, then confirm and retry. " + ex.Message }; }
    }
    public void Dispose() => _scope?.Dispose();
}
