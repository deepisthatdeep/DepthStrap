namespace DepthStrap.Recovery;

internal sealed record SignInResetPreview(string CookiePath, RegistryResetPreview Registry);
internal sealed record SignInResetOutcome(bool Complete, CookieResetResult Cookies, string Message);

/// <summary>Explicit Roblox vendor-key and cookie reset without removing other application files.</summary>
internal sealed class RobloxSignInReset(CookieReset cookies, RobloxRegistryReset registry)
{
    internal static RobloxSignInReset ForCurrentUser() => new(new CookieReset(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"), CookieReset.ClientsRunning),
        RobloxRegistryReset.ForCurrentUser());
    internal SignInResetPreview Preview() => new(cookies.Target, registry.Preview());
    internal SignInResetOutcome Execute(SignInResetPreview preview, bool confirmed)
    {
        if (!confirmed || !string.Equals(preview.CookiePath, cookies.Target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Confirm this registry and cookie reset's preview first.");
        registry.ValidatePreview(preview.Registry, true);
        // A locked or redirected cookie stops the operation before registry deletion.
        CookieResetResult removed = cookies.Reset(true);
        try
        {
            registry.Execute(preview.Registry, true);
            return new(true, removed, (removed == CookieResetResult.Removed ? "RobloxCookies.dat was deleted." : "RobloxCookies.dat was already absent.")
                + " The Roblox current-user vendor registry key and its subkeys were deleted or already absent. Sign in normally when reopening Roblox.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new(false, removed, (removed == CookieResetResult.Removed ? "RobloxCookies.dat was deleted." : "RobloxCookies.dat was already absent.")
                + " Registry deletion is incomplete. Review a new preview before retrying. " + ex.Message);
        }
    }
}
