using System.Windows;

namespace DepthStrap.Recovery;

internal static class ResetDialogs
{
    internal static async Task SignInAsync(Window owner, Action<string> report, RobloxSignInReset? resetService = null)
    {
        var service = resetService ?? RobloxSignInReset.ForCurrentUser();
        var preview = await Task.Run(service.Preview);
        if (RecoveryPrompt.Show(owner, $"Delete Roblox's current-user registry key, including its subkeys and values, and RobloxCookies.dat in both supported locations?\n\n{preview.Registry.Location}\n{preview.CookiePath}\n{preview.LocalStorageCookiePath}\n\nClose Roblox and Studio. This signs you out and resets local Roblox registry settings. It cannot be undone.",
            "Delete Roblox registry keys and cookies", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { report("Cancelled. No Roblox registry keys or cookies were removed."); return; }
        var result = await Task.Run(() => service.Execute(preview, true));
        report((result.Complete ? "Finished. " : "Incomplete. ") + result.Message);
    }

    internal static async Task AllDataAsync(Window owner, Action<string> report, string? installationExecutable = null,
        RobloxDataReset? resetService = null, RobloxRegistryReset? registryService = null)
    {
        var service = resetService ?? RobloxDataReset.ForCurrentUser(installationExecutable);
        var registry = registryService ?? RobloxRegistryReset.ForCurrentUser();
        var preview = await Task.Run(() => service.Preview());
        var registryPreview = await Task.Run(registry.Preview);
        var dialog = new RobloxDataResetDialog(owner, service, registry, new(preview, registryPreview));
        if (dialog.ShowDialog() != true) { report("Cancelled. No Roblox data was removed."); return; }
        var combined = new RobloxFullReset(dialog.Service, registry);
        var result = await Task.Run(() => combined.Execute(dialog.Preview, true));
        report((result.Complete ? "Finished. " : "Incomplete. ") + result.Message
            + $"\nRemoved: {result.Data.RemovedFiles} files, {result.Data.RemovedDirectories} folders.");
    }
}
