using System.Net.NetworkInformation;

namespace DepthStrap.Recovery;

internal sealed record AdapterInfo(string Name, string Kind, string State, long LinkBitsPerSecond, string MaskedMac);
internal static class AdapterInspection
{
    internal static AdapterInfo[] Inspect() => NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
        new AdapterInfo(Read(() => adapter.Name, "Unavailable"), Read(() => adapter.NetworkInterfaceType.ToString(), "Unavailable"),
            Read(() => adapter.OperationalStatus.ToString(), "Unavailable"), Read(() => adapter.Speed, 0L),
            Read(() => MaskMac(adapter.GetPhysicalAddress().GetAddressBytes()), "Unavailable"))).ToArray();
    internal static T Read<T>(Func<T> read, T unavailable)
    {
        try { return read(); }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException) { return unavailable; }
    }

    internal static string MaskMac(byte[] address) => address.Length != 6 ? "Unavailable" :
        "**:**:**:" + string.Join(":", address.Skip(3).Select(x => x.ToString("X2")));

    // Pure validation for future supported adapter settings; this does not write a MAC.
    internal static bool IsLocalUnicastMac(string? value)
    {
        if (value is null) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A(?:[0-9A-Fa-f]{12}|[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5})\z")) return false;
        string text = value.Replace(":", "").Replace("-", "");
        return (Convert.FromHexString(text)[0] & 3) == 2;
    }
}
