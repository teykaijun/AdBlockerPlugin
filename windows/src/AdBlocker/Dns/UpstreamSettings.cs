using System.Net;
using System.Net.Sockets;

namespace AdBlocker.Dns;

/// <summary>Turns the <c>upstream</c> entries of the configuration into servers.</summary>
public static class UpstreamSettings
{
    /// <summary>Use the DNS servers the network (or the user) configured before AdBlocker took over.</summary>
    public const string Auto = "auto";

    /// <summary>Named providers. They are reached over HTTPS so lookups are encrypted.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["cloudflare"] = ["https://1.1.1.1/dns-query", "https://1.0.0.1/dns-query"],
        ["quad9"] = ["https://9.9.9.9/dns-query", "https://149.112.112.112/dns-query"],
        ["google"] = ["https://8.8.8.8/dns-query", "https://8.8.4.4/dns-query"],
    };

    /// <summary>Used when nothing else is available.</summary>
    public static readonly string[] Fallback = ["1.1.1.1", "9.9.9.9", "2606:4700:4700::1111"];

    /// <summary>A description of what is wrong with <paramref name="entry"/>, or null if it is valid.</summary>
    public static string? Validate(string entry)
    {
        if (entry.Equals(Auto, StringComparison.OrdinalIgnoreCase) || Presets.ContainsKey(entry)) return null;
        if (TryParseAddress(entry, out var endpoint))
        {
            return IsUsable(endpoint) ? null : $"\"{entry}\" cannot be used: that is AdBlocker itself, or not a real server address.";
        }
        if (Uri.TryCreate(entry, UriKind.Absolute, out var uri))
        {
            return uri.Scheme == Uri.UriSchemeHttps ? null : $"\"{entry}\" must use https://.";
        }
        return $"\"{entry}\" is not an IP address, an https:// URL, \"auto\" or one of: {string.Join(", ", Presets.Keys)}.";
    }

    public static List<IUpstream> Create(IEnumerable<string> entries, IEnumerable<IPAddress> automatic, HttpClient doh)
    {
        var servers = new List<IUpstream>();
        foreach (var entry in entries)
        {
            if (entry.Equals(Auto, StringComparison.OrdinalIgnoreCase))
            {
                servers.AddRange(automatic.Select(a => new UdpUpstream(a)));
            }
            else if (Presets.TryGetValue(entry, out var urls))
            {
                servers.AddRange(urls.Select(u => new DohUpstream(new Uri(u), doh)));
            }
            else if (TryParseAddress(entry, out var endpoint))
            {
                if (IsUsable(endpoint)) servers.Add(new UdpUpstream(endpoint));
            }
            else if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                servers.Add(new DohUpstream(uri, doh));
            }
        }

        var unique = servers.DistinctBy(s => s.Name).ToList();
        return unique.Count > 0 ? unique : [.. Fallback.Select(f => new UdpUpstream(IPAddress.Parse(f)))];
    }

    /// <summary>
    /// True for addresses of real DNS servers on the network: not loopback (that would be
    /// AdBlocker), unspecified, or one of the fec0:: placeholders Windows reports.
    /// </summary>
    public static bool IsUsable(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any)
        && !address.IsIPv6SiteLocal;

    /// <summary>
    /// A configured server. Loopback is fine on another port (another local resolver),
    /// but not on port 53, where AdBlocker itself listens.
    /// </summary>
    public static bool IsUsable(IPEndPoint endpoint) =>
        IPAddress.IsLoopback(endpoint.Address) ? endpoint.Port != 53 : IsUsable(endpoint.Address);

    private static bool TryParseAddress(string entry, out IPEndPoint endpoint)
    {
        endpoint = null!;
        if (!IPEndPoint.TryParse(entry, out var parsed)) return false;
        if (parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        endpoint = parsed.Port == 0 ? new IPEndPoint(parsed.Address, 53) : parsed;
        return true;
    }
}
