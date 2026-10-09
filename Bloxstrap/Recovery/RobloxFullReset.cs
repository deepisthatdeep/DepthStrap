namespace DepthStrap.Recovery;

internal sealed record FullResetPreview(ResetPreview Data, RegistryResetPreview Registry);
internal sealed record FullResetOutcome(bool Complete, ResetOutcome Data, string Message);

/// <summary>Validate both stores before permanently removing selected Roblox data.</summary>
internal sealed class RobloxFullReset(RobloxDataReset data, RobloxRegistryReset registry)
{
    internal FullResetPreview Preview(CancellationToken token = default) => new(data.Preview(token), registry.Preview());
    internal FullResetOutcome Execute(FullResetPreview preview, bool confirmed, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        registry.ValidatePreview(preview.Registry, confirmed);
        var removed = data.Execute(preview.Data, confirmed, token);
        if (!removed.Complete) return new(false, removed, removed.Message + " Registry data was retained.");
        try
        {
            token.ThrowIfCancellationRequested();
            registry.Execute(preview.Registry, true);
            return new(true, removed, removed.Message + " The Roblox current-user registry tree was deleted or already absent.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(false, removed, "Folder deletion completed, but registry deletion is incomplete. Some data has already been removed; review a new preview before retrying.");
        }
    }
}
