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
    public static List<IPAddress> OwnAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IsPrivate(ip) && !ip.Equals(IPAddress.Loopback)) addresses.Add(ip);
            }
        }
        return addresses;
    }
}
