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

    /// <summary>Domains allowed until a time ("adblocker allow example.com --for 10m").</summary>
    public Dictionary<string, DateTimeOffset> TemporarilyAllowed { get; set; } = [];

    /// <summary>Nothing is blocked until this time ("adblocker pause"); null when not paused.</summary>
    public DateTimeOffset? PausedUntil { get; set; }

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
            // Read in one go. A command or the service replacing the file meanwhile has to wait until
            // it is closed, whatever the sharing here; AtomicFile.Replace retries for that moment.
            using var reader = new StreamReader(new FileStream(paths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return Normalize(JsonSerializer.Deserialize(reader.ReadToEnd(), AppJson.Default.AppConfig) ?? new AppConfig());
        }
        catch (JsonException e)
        {
            throw new UserError($"{paths.ConfigFile} is not valid JSON: {e.Message}");
        }
    }

    public static void Save(AppPaths paths, AppConfig config)
    {
        paths.EnsureCreated();
        AtomicFile.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(config, AppJson.Default.AppConfig), durable: true);
    }

    /// <summary>
    /// Repairs what a hand edit can leave behind that valid JSON allows: null lists, null or blank
    /// entries, community lists without an id, and the same list twice (the last one wins).
    /// </summary>
    internal static AppConfig Normalize(AppConfig config)
    {
        var defaults = new AppConfig();
        config.Lists ??= defaults.Lists;
        config.CommunityLists ??= defaults.CommunityLists;
        config.BlockedDomains ??= defaults.BlockedDomains;
        config.AllowedDomains ??= defaults.AllowedDomains;
        config.TemporarilyAllowed ??= defaults.TemporarilyAllowed;
        config.Upstream ??= defaults.Upstream;
        config.ExcludedAdapters ??= defaults.ExcludedAdapters;

        config.BlockedDomains.RemoveAll(string.IsNullOrWhiteSpace);
        config.AllowedDomains.RemoveAll(string.IsNullOrWhiteSpace);
        config.Upstream.RemoveAll(string.IsNullOrWhiteSpace);
        config.ExcludedAdapters.RemoveAll(string.IsNullOrWhiteSpace);
        foreach (var key in config.Lists.Keys.Where(string.IsNullOrWhiteSpace).ToList()) config.Lists.Remove(key);
        foreach (var key in config.TemporarilyAllowed.Keys.Where(string.IsNullOrWhiteSpace).ToList()) config.TemporarilyAllowed.Remove(key);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lists = new List<CommunityListSetting>();
        for (var i = config.CommunityLists.Count - 1; i >= 0; i--)
        {
            var list = config.CommunityLists[i];
            if (list is not null && !string.IsNullOrWhiteSpace(list.Id) && seen.Add(list.Id)) lists.Add(list);
        }
        lists.Reverse();
        config.CommunityLists = lists;
        return config;
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
[JsonSerializable(typeof(List<CommunityListInfo>))]
[JsonSerializable(typeof(Dictionary<string, ListStatus>))]
[JsonSerializable(typeof(StatsSnapshot))]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(DnsBackup))]
internal sealed partial class AppJson : JsonSerializerContext;
