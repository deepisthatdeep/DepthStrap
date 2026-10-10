using System.Net;
using System.Net.Sockets;

namespace Bloxstrap.Networking
{
    internal sealed record RoutingTarget(string City, string Country, string Address);
    internal static class RoutingTargetDiscovery
    {
        // Compare city names across the peering and datacenter inventories without
        // treating neighbouring cities, state aliases or countries as one location.
        internal static bool CoversLocation(IEnumerable<RoutingTarget> targets, string city, string country) =>
            targets.Any(target => target.Country.Trim().Equals(country.Trim(), StringComparison.OrdinalIgnoreCase) &&
                CityKey(target.City) == CityKey(city));

        private static string CityKey(string city)
        {
            string normalized = city.Trim().Normalize(System.Text.NormalizationForm.FormD);
            string key = string.Concat(normalized.Where(character =>
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark))
                .Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
            key = Regex.Replace(key, @"\s+", " ");
            return key switch
            {
                "new york city" or "nyc" => "new york",
                "frankfurt am main" => "frankfurt",
                "santiago de queretaro" => "queretaro",
                "sao paulo/sp" => "sao paulo",
                _ => key
            };
        }

        internal static List<RoutingTarget> Parse(string linksJson, string exchangesJson)
        {
            using var links = JsonDocument.Parse(linksJson);
            using var exchanges = JsonDocument.Parse(exchangesJson);
            var cities = exchanges.RootElement.GetProperty("data").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt32(),
                x => (City: x.GetProperty("city").GetString() ?? "", Country: x.GetProperty("country").GetString() ?? ""));
            return links.RootElement.GetProperty("data").EnumerateArray()
                .Where(x => x.GetProperty("status").GetString() == "ok" && x.GetProperty("operational").GetBoolean())
                .SelectMany(x => new[] { "ipaddr4", "ipaddr6" }.Select(field =>
                    (Ix: x.GetProperty("ix_id").GetInt32(), Ip: x.TryGetProperty(field, out var address) && address.ValueKind == JsonValueKind.String ? address.GetString() : null)))
                .Where(x => cities.ContainsKey(x.Ix) && IPAddress.TryParse(x.Ip, out var ip) && !IPAddress.IsLoopback(ip) &&
                    !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !ip.IsIPv6LinkLocal)
                .Select(x => new RoutingTarget(cities[x.Ix].City, cities[x.Ix].Country, x.Ip!))
                .Where(x => x.City.Length > 0).DistinctBy(x => x.Address).ToList();
        }
    }
}
