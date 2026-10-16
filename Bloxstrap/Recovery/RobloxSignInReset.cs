namespace DepthStrap.Recovery;

internal sealed record SignInResetPreview(string CookiePath, RegistryResetPreview Registry, string? LocalStorageCookiePath = null);
internal sealed record SignInResetOutcome(bool Complete, CookieResetResult Cookies, string Message);

/// <summary>Explicit Roblox vendor-key and cookie reset without removing other application files.</summary>
internal sealed class RobloxSignInReset(CookieReset cookies, RobloxRegistryReset registry, CookieReset? localStorageCookies = null)
{
    internal static RobloxSignInReset ForCurrentUser() => ForRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"), RobloxRegistryReset.ForCurrentUser(), CookieReset.ClientsRunning);
    internal static RobloxSignInReset ForRoot(string root, RobloxRegistryReset registry, Func<bool> running) =>
        new(new CookieReset(root, running), registry, new CookieReset(Path.Combine(root, "LocalStorage"), running));
    internal SignInResetPreview Preview() => new(cookies.Target, registry.Preview(), localStorageCookies?.Target);
    internal SignInResetOutcome Execute(SignInResetPreview preview, bool confirmed)
    {
        if (!confirmed || !string.Equals(preview.CookiePath, cookies.Target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(preview.LocalStorageCookiePath, localStorageCookies?.Target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Confirm this registry and cookie reset's preview first.");
        registry.ValidatePreview(preview.Registry, true);
        // A locked or redirected cookie stops the operation before registry deletion.
        using var rootCookie = cookies.Prepare(true);
        using var storageCookie = localStorageCookies?.Prepare(true);
        CookieResetResult removed = CookieResetResult.NotPresent;
        try
        {
            removed = rootCookie.Delete();
            if (storageCookie?.Delete() == CookieResetResult.Removed) removed = CookieResetResult.Removed;
            registry.Execute(preview.Registry, true);
            return new(true, removed, (removed == CookieResetResult.Removed ? "RobloxCookies.dat was deleted." : "RobloxCookies.dat was already absent.")
                + " The Roblox current-user vendor registry key and its subkeys were deleted or already absent. Sign in normally when reopening Roblox.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(false, removed, (removed == CookieResetResult.Removed ? "RobloxCookies.dat was deleted." : "RobloxCookies.dat was already absent.")
                + " Registry deletion is incomplete. Review a new preview before retrying. " + ex.Message);
        }
    }
}
