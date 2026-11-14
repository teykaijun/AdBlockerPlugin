using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AdBlocker.Dns;

/// <summary>
/// Which clients may ask AdBlocker when it serves the local network: this PC, the home
/// network, and private overlay networks such as Tailscale. Everything else is ignored, so
/// AdBlocker can never act as an open resolver for the internet.
/// </summary>
public static class LocalNetwork
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                172 => octets[1] >= 16 && octets[1] <= 31,     // 172.16.0.0/12
                192 => octets[1] == 168,                       // 192.168.0.0/16
                169 => octets[1] == 254,                       // 169.254.0.0/16, link-local
                100 => octets[1] >= 64 && octets[1] <= 127,    // 100.64.0.0/10, used by Tailscale
                _ => false,
            };
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    /// <summary>This PC's addresses on the local network, for people to type into a phone.</summary>
    public static List<IPAddress> OwnAddresses() => LanAddresses().Select(a => a.Address).ToList();

    /// <summary>
    /// The addresses other devices can reach this PC on, with the adapter each belongs to. Only
    /// adapters with a default gateway count (the Wi-Fi or Ethernet the phone is on), which leaves
    /// out Hyper-V, WSL, VirtualBox and VMware adapters, plus Tailscale's addresses, which have none.
    /// </summary>
    public static List<LanAddress> LanAddresses()
    {
        var addresses = new List<LanAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
            foreach (var address in properties.UnicastAddresses)
            {
                if (IsLanAddress(address.Address, hasGateway)) addresses.Add(new LanAddress(address.Address, adapter.Name));
            }
        }
        return addresses;
    }

    /// <summary>Whether a phone could use <paramref name="address"/>: private IPv4 on an adapter with a gateway, or Tailscale.</summary>
    internal static bool IsLanAddress(IPAddress address, bool adapterHasGateway) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(address)
        && IsPrivate(address)
        && (adapterHasGateway || IsCarrierGradeNat(address));

    /// <summary>
    /// Every address of this PC, without IPv6 scope ids. A DNS server there is AdBlocker itself
    /// (or nothing at all), never a server to forward lookups to.
    /// </summary>
    public static IReadOnlySet<IPAddress> ThisComputer()
    {
        var addresses = new HashSet<IPAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses) addresses.Add(Plain(address.Address));
            }
        }
        catch (NetworkInformationException)
        {
            // Without the list, nothing is left out; loopback is refused anyway.
        }
        return addresses;
    }

    /// <summary>
    /// The addresses besides loopback (which the settings refuse anyway) that AdBlocker answers
    /// DNS on: all of <see cref="ThisComputer"/> while it serves the local network, none otherwise.
    /// A DNS server on one of them would be AdBlocker asking itself; without "lan on" it can only
    /// be another program, such as a resolver of the user's own.
    /// </summary>
    public static IReadOnlySet<IPAddress> AnsweredByAdBlocker(bool servingLan) =>
        servingLan ? ThisComputer() : new HashSet<IPAddress>();

    /// <summary><paramref name="address"/> as IPv4 if it is IPv4 mapped into IPv6, and without an IPv6 scope id.</summary>
    public static IPAddress Plain(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address;
    }

    /// <summary>100.64.0.0/10, which Tailscale uses.</summary>
    private static bool IsCarrierGradeNat(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return octets.Length == 4 && octets[0] == 100 && octets[1] is >= 64 and <= 127;
    }
}

/// <summary>An address of this PC other devices can use, and the adapter it belongs to.</summary>
public readonly record struct LanAddress(IPAddress Address, string Adapter)
{
    /// <summary>For example "192.168.1.50 (Wi-Fi)".</summary>
    public override string ToString() => $"{Address} ({Adapter})";
}
