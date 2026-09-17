using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using AdBlocker.Config;
using AdBlocker.Dns;
using Microsoft.Win32;

namespace AdBlocker.Platform;

/// <summary>What the adapters' DNS settings were before AdBlocker changed them.</summary>
public sealed class DnsBackup
{
    public List<SavedAdapter> Adapters { get; set; } = [];
}

public sealed class SavedAdapter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Static IPv4 DNS servers as stored by Windows; empty means "from DHCP".</summary>
    public string IPv4 { get; set; } = "";

    /// <summary>Static IPv6 DNS servers; empty means automatic.</summary>
    public string IPv6 { get; set; } = "";
}

public sealed record AdapterInfo(string Id, string Name, string Description, IReadOnlyList<IPAddress> DnsServers);

/// <summary>
/// Points the network adapters at AdBlocker's DNS server on 127.0.0.1 / ::1 and puts
/// the original settings back afterwards. The originals are saved to disk before
/// anything changes, so a crash or power cut can still be undone.
/// </summary>
/// <remarks>
/// Every adapter that has internet access is changed, not just the main one: Windows
/// asks the DNS servers of all adapters at once and uses the first answer, so a single
/// unchanged adapter would let ads through.
/// </remarks>
public sealed class SystemDns(AppPaths paths)
{
    public const string LoopbackIPv4 = "127.0.0.1";
    public const string LoopbackIPv6 = "::1";

    private const string IPv4Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string IPv6Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";

    /// <summary>True while adapters are (or may still be) pointed at AdBlocker.</summary>
    public bool IsApplied => File.Exists(paths.DnsStateFile);

    /// <summary>Adapters that are connected and can reach a network, minus the excluded ones.</summary>
    public static IReadOnlyList<AdapterInfo> FindAdapters(IReadOnlyCollection<string> excluded)
    {
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (excluded.Any(x => nic.Name.Contains(x, StringComparison.OrdinalIgnoreCase)
                                  || nic.Description.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var properties = nic.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
            var dns = properties.DnsAddresses.ToList();
            // Adapters without a gateway (Hyper-V, VirtualBox host-only, ...) only matter if they bring their own DNS.
            if (!hasGateway && !dns.Any(UpstreamSettings.IsUsable)) continue;

            adapters.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, dns));
        }
        return adapters;
    }

    /// <summary>The DNS servers that are in use right now, ignoring AdBlocker itself.</summary>
    public static IReadOnlyList<IPAddress> CurrentServers(IReadOnlyCollection<string> excluded) =>
        FindAdapters(excluded).SelectMany(a => a.DnsServers).Where(UpstreamSettings.IsUsable).Distinct().ToList();

    /// <summary>
    /// The servers the adapters would use without AdBlocker: the saved static settings, the
    /// servers handed out by DHCP (Windows keeps them even while overridden) and anything
    /// currently configured that is not AdBlocker.
    /// </summary>
    public IReadOnlyList<IPAddress> OriginalServers(IReadOnlyCollection<string> excluded)
    {
        var backup = LoadBackup();
        var servers = new List<IPAddress>();
        foreach (var adapter in FindAdapters(excluded))
        {
            var saved = backup?.Adapters.Find(a => a.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
            servers.AddRange(ParseServers(saved?.IPv4 ?? ReadRegistry(IPv4Interfaces + adapter.Id, "NameServer")));
            servers.AddRange(ParseServers(saved?.IPv6 ?? ReadRegistry(IPv6Interfaces + adapter.Id, "NameServer")));
            servers.AddRange(ParseServers(ReadRegistry(IPv4Interfaces + adapter.Id, "DhcpNameServer")));
            servers.AddRange(adapter.DnsServers);
        }
        return servers.Where(UpstreamSettings.IsUsable).Distinct().ToList();
    }

    /// <summary>Points every adapter at AdBlocker. Returns the names of the adapters that changed.</summary>
    public IReadOnlyList<string> Apply(IReadOnlyCollection<string> excluded, bool includeIPv6)
    {
        var adapters = FindAdapters(excluded);
        var backup = LoadBackup() ?? new DnsBackup();
        foreach (var adapter in adapters)
        {
            if (backup.Adapters.Any(a => a.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase))) continue;
            backup.Adapters.Add(new SavedAdapter
            {
                Id = adapter.Id,
                Name = adapter.Name,
                IPv4 = WithoutLoopback(ReadRegistry(IPv4Interfaces + adapter.Id, "NameServer")),
                IPv6 = WithoutLoopback(ReadRegistry(IPv6Interfaces + adapter.Id, "NameServer")),
            });
        }
        // Save first, so a crash half-way through can still be undone.
        AtomicFile.WriteAllText(paths.DnsStateFile, JsonSerializer.Serialize(backup, AppJson.Default.DnsBackup));

        var changed = new List<string>();
        foreach (var adapter in adapters)
        {
            var touched = SetIfDifferent(adapter.Id, ipv6: false, LoopbackIPv4);
            if (includeIPv6)
            {
                // IPv6 can be switched off per adapter; the IPv4 setting is what matters then.
                try
                {
                    touched |= SetIfDifferent(adapter.Id, ipv6: true, LoopbackIPv6);
                }
                catch (Win32Exception)
                {
                }
            }
            if (touched) changed.Add(adapter.Name);
        }
        if (changed.Count > 0) FlushCache();
        return changed;
    }

    /// <summary>Puts the saved settings back. Returns the names of the adapters restored.</summary>
    public IReadOnlyList<string> Restore()
    {
        var backup = LoadBackup();
        if (backup is null) return [];
        foreach (var adapter in backup.Adapters)
        {
            RestoreServers(adapter.Id, ipv6: false, adapter.IPv4);
            RestoreServers(adapter.Id, ipv6: true, adapter.IPv6);
        }
        File.Delete(paths.DnsStateFile);
        FlushCache();
        return backup.Adapters.Select(a => a.Name).ToList();
    }

    public DnsBackup? LoadBackup()
    {
        if (!File.Exists(paths.DnsStateFile)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(paths.DnsStateFile), AppJson.Default.DnsBackup);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The static servers Windows stores for an adapter, read through the official API (for diagnostics).</summary>
    public static string? ReadServersWithApi(string adapterId, bool ipv6)
    {
        var settings = new NativeMethods.DnsInterfaceSettings
        {
            Version = NativeMethods.DnsInterfaceSettingsVersion1,
            Flags = ipv6 ? NativeMethods.DnsSettingIPv6 : 0,
        };
        try
        {
            if (NativeMethods.GetInterfaceDnsSettings(Guid.Parse(adapterId), ref settings) != 0) return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        try
        {
            // A null pointer just means "no static servers".
            return Marshal.PtrToStringUni(settings.NameServer) ?? "";
        }
        finally
        {
            NativeMethods.FreeInterfaceDnsSettings(ref settings);
        }
    }

    public static string? ReadStaticServers(string adapterId, bool ipv6) =>
        ReadRegistry((ipv6 ? IPv6Interfaces : IPv4Interfaces) + adapterId, "NameServer");

    /// <summary>Whether two DNS server settings list the same servers, whatever the separators.</summary>
    public static bool SameServers(string? a, string? b) => ParseServers(a).ToHashSet().SetEquals(ParseServers(b));

    public static bool IsLoopbackSetting(string? servers)
    {
        var parsed = ParseServers(servers);
        return parsed.Count > 0 && parsed.All(IPAddress.IsLoopback);
    }

    public static void FlushCache()
    {
        try
        {
            NativeMethods.DnsFlushResolverCache();
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static bool SetIfDifferent(string adapterId, bool ipv6, string servers)
    {
        if (string.Equals(ReadStaticServers(adapterId, ipv6)?.Trim(), servers, StringComparison.OrdinalIgnoreCase)) return false;
        SetServers(adapterId, ipv6, servers);
        return true;
    }

    private static void SetServers(string adapterId, bool ipv6, string servers)
    {
        var settings = new NativeMethods.DnsInterfaceSettings
        {
            Version = NativeMethods.DnsInterfaceSettingsVersion1,
            Flags = NativeMethods.DnsSettingNameServer | (ipv6 ? NativeMethods.DnsSettingIPv6 : 0),
        };
        var text = Marshal.StringToHGlobalUni(servers);
        try
        {
            settings.NameServer = text;
            uint error;
            try
            {
                error = NativeMethods.SetInterfaceDnsSettings(Guid.Parse(adapterId), ref settings);
            }
            catch (EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException("AdBlocker needs Windows 10 version 2004 or later.");
            }
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private static void RestoreServers(string adapterId, bool ipv6, string servers)
    {
        // Leave the adapter alone if someone changed its DNS settings in the meantime.
        if (!IsLoopbackSetting(ReadStaticServers(adapterId, ipv6))) return;
        try
        {
            SetServers(adapterId, ipv6, servers);
        }
        catch (Win32Exception)
        {
            // The adapter is not connected right now. Fix the stored setting so it is
            // right when the adapter comes back.
            using var key = Registry.LocalMachine.OpenSubKey((ipv6 ? IPv6Interfaces : IPv4Interfaces) + adapterId, writable: true);
            key?.SetValue("NameServer", servers, RegistryValueKind.String);
        }
    }

    /// <summary>An adapter already pointing at AdBlocker (after a crash) counts as "automatic".</summary>
    private static string WithoutLoopback(string? servers) =>
        servers is null || IsLoopbackSetting(servers) ? "" : servers.Trim();

    private static string? ReadRegistry(string keyPath, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(name) as string;
    }

    private static List<IPAddress> ParseServers(string? servers) =>
        (servers ?? "")
        .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(s => IPAddress.TryParse(s, out var address) ? address : null)
        .OfType<IPAddress>()
        .ToList();
}
