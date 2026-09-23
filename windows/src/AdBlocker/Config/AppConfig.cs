using System.Text.Json;
using System.Text.Json.Serialization;
using AdBlocker.Core;
using AdBlocker.Filtering;
using AdBlocker.Platform;

namespace AdBlocker.Config;

/// <summary>The contents of config.json.</summary>
public sealed class AppConfig
{
    /// <summary>Built-in lists the user switched on or off; missing ids use the list's default.</summary>
    public Dictionary<string, bool> Lists { get; set; } = [];

    /// <summary>State of the community lists: presets the user toggled plus lists added by URL.</summary>
    public List<CommunityListSetting> CommunityLists { get; set; } = [];

    public List<string> BlockedDomains { get; set; } = [];

    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>Where allowed lookups go: "auto", a preset name, IP addresses or https:// URLs.</summary>
    public List<string> Upstream { get; set; } = ["auto"];

    /// <summary>
    /// Network adapters (matched against name or description) whose DNS settings are left
    /// alone. VPN software manages its own DNS, and changing it breaks names on the VPN.
    /// </summary>
    public List<string> ExcludedAdapters { get; set; } =
    [
        "Tailscale", "WireGuard", "Wintun", "OpenVPN", "TAP-Windows", "ZeroTier", "NordLynx",
        "Mullvad", "ProtonVPN", "AnyConnect", "Fortinet", "GlobalProtect", "PANGP", "Pulse Secure",
    ];

    /// <summary>Also write allowed lookups to the query log (blocked ones are always written).</summary>
    public bool LogAllowedQueries { get; set; }

    /// <summary>Ask GitHub once a day whether a newer AdBlocker is out, and say so in "status".</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Also answer DNS for other devices on the local network (phones, tablets, TVs), so an
    /// iPhone can point its Wi-Fi DNS at this PC. Only private addresses are ever answered.
    /// Switch it on with "adblocker lan on", which also opens the firewall for private networks.
    /// </summary>
    public bool ListenOnLan { get; set; }
}

/// <summary>What the last update check found, kept in update.json for "status" to show.</summary>
public sealed class UpdateState
{
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>The newer version on the releases page, or null when this build is the latest.</summary>
    public string? Version { get; set; }

    public string? PageUrl { get; set; }
}

public sealed class CommunityListSetting
{
    public string Id { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>Only for lists added by URL.</summary>
    public string? Title { get; set; }

    /// <summary>Only for lists added by URL.</summary>
    public string? Url { get; set; }
}

public static class ConfigStore
{
    public static AppConfig Load(AppPaths paths)
    {
        if (!File.Exists(paths.ConfigFile)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(paths.ConfigFile), AppJson.Default.AppConfig) ?? new AppConfig();
        }
        catch (JsonException e)
        {
            throw new UserError($"{paths.ConfigFile} is not valid JSON: {e.Message}");
        }
    }

    public static void Save(AppPaths paths, AppConfig config)
    {
        paths.EnsureCreated();
        AtomicFile.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(config, AppJson.Default.AppConfig));
    }

    /// <summary>Loads, changes and saves the configuration. A running service picks the change up.</summary>
    public static AppConfig Update(AppPaths paths, Action<AppConfig> change)
    {
        var config = Load(paths);
        change(config);
        Save(paths, config);
        return config;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<BuiltInListInfo>))]
[JsonSerializable(typeof(Dictionary<string, ListStatus>))]
[JsonSerializable(typeof(StatsSnapshot))]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(DnsBackup))]
internal sealed partial class AppJson : JsonSerializerContext;
