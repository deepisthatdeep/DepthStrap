using System.Security.Cryptography;

namespace DepthStrap.Toolkit;

public sealed record MacAdapter(Guid Id, bool Supported, string EffectiveAddress, string? Override);
public sealed record MacBackup(Guid Adapter, string OriginalEffectiveAddress, string? OriginalOverride, string AppliedAddress);
public enum ConnectionState { Online, Offline, Unknown }
public enum MacResultState { Applied, RestoreOffered, Restored, Unavailable, UserChangePreserved, Failed }
public sealed record MacResult(MacResultState State, string Message);

/// <summary>Driver-supported MAC transaction. Backend must serialize writes and protect backup storage.</summary>
public interface IMacChangeBackend
{
    ValueTask<IAsyncDisposable> LockAsync(Guid adapter, CancellationToken token);
    Task<MacAdapter?> ReadAsync(Guid adapter, CancellationToken token);
    Task<MacBackup?> LoadBackupAsync(Guid adapter, CancellationToken token);
    Task SaveBackupAsync(MacBackup backup, CancellationToken token);
    Task DeleteBackupAsync(Guid adapter, CancellationToken token);
    Task SetOverrideAsync(Guid adapter, string? address, CancellationToken token);
    Task<ConnectionState> CheckConnectivityAsync(Guid adapter, CancellationToken token);
}

public sealed class MacChange(IMacChangeBackend backend)
{
    public static string NormalizeAddress(string value)
    {
        if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value,
                @"\A(?:[0-9A-Fa-f]{12}|[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5})\z"))
            throw new ArgumentException("Use a complete six-byte MAC address.");
        return value.Replace(":", "").Replace("-", "").ToUpperInvariant();
    }
    public static string GenerateLocalAddress()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)((bytes[0] & 0xfc) | 2);
        return Convert.ToHexString(bytes);
    }
    public static bool IsLocalUnicast(string value) { try { return (Convert.FromHexString(NormalizeAddress(value))[0] & 3) == 2; } catch (ArgumentException) { return false; } }
    public static bool IsUnicast(string value)
    {
        try { var bytes = Convert.FromHexString(NormalizeAddress(value)); return (bytes[0] & 1) == 0 && bytes.Any(value => value != 0); }
        catch (ArgumentException) { return false; }
    }
    // An empty REG_SZ is distinct from an absent override and must survive restore.
    public static string? NormalizeOverride(string? value) => value is null ? null : value.Length == 0 ? "" : NormalizeAddress(value);

    public async Task<MacResult> ApplyAsync(Guid adapter, string address, bool confirmed, CancellationToken token = default)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm the adapter interruption and MAC change first.");
        if (adapter == Guid.Empty) throw new ArgumentException("Select one adapter.");
        address = NormalizeAddress(address);
        if (!IsLocalUnicast(address)) throw new ArgumentException("Use a locally administered unicast MAC address.");
        await using var gate = await backend.LockAsync(adapter, token);
        var current = await backend.ReadAsync(adapter, token);
        if (current is null || !current.Supported) return new(MacResultState.Unavailable, "This adapter does not expose a supported MAC setting. No change was attempted.");
        if (current.Id != adapter) throw new IOException("Adapter identity changed during selection.");
        if (await backend.LoadBackupAsync(adapter, token) is not null)
            throw new InvalidOperationException("Restore this adapter's existing backup before applying another MAC change.");
        string original = NormalizeAddress(current.EffectiveAddress);
        if (!IsUnicast(original)) return new(MacResultState.Unavailable, "The adapter has no valid six-byte unicast address. No change was attempted.");
        string? originalOverride = NormalizeOverride(current.Override);
        if (originalOverride is { Length: > 0 } && !IsUnicast(originalOverride))
            return new(MacResultState.Unavailable, "The existing driver override cannot be safely restored. No change was attempted.");
        var backup = new MacBackup(adapter, original, originalOverride, address);
        await backend.SaveBackupAsync(backup, token);
        if (await backend.LoadBackupAsync(adapter, token) != backup) throw new IOException("The backup could not be verified. No MAC change was attempted.");
        // Cancellation remains safe until the first write. Once a write may have
        // occurred, verification/rollback cannot be cancelled halfway through.
        token.ThrowIfCancellationRequested();
        try
        {
            await backend.SetOverrideAsync(adapter, address, CancellationToken.None);
            var actual = await backend.ReadAsync(adapter, CancellationToken.None);
            if (actual is null || actual.Id != adapter || NormalizeAddress(actual.EffectiveAddress) != address || NormalizeOverride(actual.Override) != address)
                throw new IOException("The driver did not activate the requested address.");
            var connectivity = await backend.CheckConnectivityAsync(adapter, CancellationToken.None);
            return connectivity switch
            {
                ConnectionState.Online => new(MacResultState.Applied, "MAC change verified. Connectivity check passed; Restore is available."),
                ConnectionState.Offline => new(MacResultState.RestoreOffered, "MAC changed, but connectivity checks failed. Restore the previous MAC?"),
                _ => new(MacResultState.RestoreOffered, "MAC changed; connectivity could not be established. Restore the previous MAC?")
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MacResult restored;
            try { restored = await RestoreLockedAsync(backup); }
            catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
            { return new(MacResultState.Failed, "MAC change failed and restoration could not be verified. The original backup was retained; retry Restore when the adapter is available."); }
            return new(restored.State == MacResultState.Restored ? MacResultState.Failed : restored.State,
                restored.State == MacResultState.Restored ? "MAC change failed. The original setting and effective address were restored." : "MAC change failed. " + restored.Message);
        }
    }
    public async Task<MacResult> RestoreAsync(Guid adapter, bool confirmed, CancellationToken token = default)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm restoring this adapter first.");
        if (adapter == Guid.Empty) throw new ArgumentException("Select one adapter.");
        await using var gate = await backend.LockAsync(adapter, token);
        var backup = await backend.LoadBackupAsync(adapter, token);
        if (backup is null) return new(MacResultState.Unavailable, "No backup exists for this adapter.");
        if (backup.Adapter != adapter) throw new IOException("The backup belongs to another adapter.");
        token.ThrowIfCancellationRequested();
        return await RestoreLockedAsync(backup);
    }
    private async Task<MacResult> RestoreLockedAsync(MacBackup backup)
    {
        // Validate even protected records; no paths or command text are accepted.
        if (backup.Adapter == Guid.Empty || !IsLocalUnicast(backup.AppliedAddress)) throw new IOException("Invalid MAC backup.");
        string effective = NormalizeAddress(backup.OriginalEffectiveAddress);
        string? originalOverride = NormalizeOverride(backup.OriginalOverride);
        if (!IsUnicast(effective) || (originalOverride is { Length: > 0 } && !IsUnicast(originalOverride))) throw new IOException("Invalid original MAC backup.");
        try
        {
            var current = await backend.ReadAsync(backup.Adapter, CancellationToken.None);
            if (current is null || !current.Supported) return new(MacResultState.Unavailable, "Adapter unavailable. Its backup was retained.");
            if (current.Id != backup.Adapter) throw new IOException("Adapter identity changed during restoration.");
            string? value = NormalizeOverride(current.Override);
            if (value != NormalizeAddress(backup.AppliedAddress) && value != originalOverride)
                return new(MacResultState.UserChangePreserved, "A later MAC setting was detected and preserved. The original backup remains available.");
            if (value == originalOverride && NormalizeAddress(current.EffectiveAddress) == effective)
            {
                await backend.DeleteBackupAsync(backup.Adapter, CancellationToken.None);
                return new(MacResultState.Restored, "The original override state and effective MAC address were already restored; the completed backup was removed.");
            }
            await backend.SetOverrideAsync(backup.Adapter, originalOverride, CancellationToken.None);
            var actual = await backend.ReadAsync(backup.Adapter, CancellationToken.None);
            if (actual is null || actual.Id != backup.Adapter || NormalizeAddress(actual.EffectiveAddress) != effective || NormalizeOverride(actual.Override) != originalOverride)
                return new(MacResultState.Failed, "Restore could not be verified. The backup was retained for retry.");
            await backend.DeleteBackupAsync(backup.Adapter, CancellationToken.None);
            return new(MacResultState.Restored, "The original override state and effective MAC address were restored.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(MacResultState.Failed, "Restore failed. The backup was retained for retry."); }
    }
}
