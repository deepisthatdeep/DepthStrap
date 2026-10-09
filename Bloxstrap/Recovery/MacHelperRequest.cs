using DepthStrap.Toolkit;

namespace DepthStrap.Recovery;

internal sealed record MacHelperRequest(bool Restoring, Guid Adapter, string? Address)
{
    internal static MacHelperRequest Parse(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("--mac-apply" or "--mac-restore"))
            throw new ArgumentException("Invalid MAC helper request.");
        bool restoring = args[0] == "--mac-restore";
        if (args.Length != (restoring ? 2 : 3) || !Guid.TryParseExact(args[1], "D", out var adapter) || adapter == Guid.Empty)
            throw new ArgumentException("Select one adapter using its complete identifier.");
        string? address = restoring ? null : MacChange.NormalizeAddress(args[2]);
        if (!restoring && !MacChange.IsLocalUnicast(address!)) throw new ArgumentException("Use a locally administered unicast MAC address.");
        return new(restoring, adapter, address);
    }

    internal string[] Arguments() => Restoring
        ? new[] { "--mac-restore", Adapter.ToString("D") }
        : new[] { "--mac-apply", Adapter.ToString("D"), Address! };
}
