using System.Text.Json;
using AdBlocker.Config;

namespace AdBlocker.Filtering;

/// <summary>An entry of filters/lists.json.</summary>
public sealed class BuiltInListInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool EnabledByDefault { get; set; }
}

public sealed record CommunityList(string Id, string Title, string Url, string Description, bool Enabled, bool Custom);

public sealed class ListStatus
{
    public DateTimeOffset? UpdatedAt { get; set; }
    public int Domains { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// The built-in lists (embedded from the repository's filters/ folder), the optional
/// community lists (downloaded on request), and the user's own rules.
/// </summary>
public sealed class FilterLists(AppPaths paths)
{
    /// <summary>Tells Firefox not to switch to its own DNS-over-HTTPS, which would skip the filter.</summary>
    public const string FirefoxDohCanary = "use-application-dns.net";

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private const long MaxDownloadBytes = 50 * 1024 * 1024;

    /// <summary>The same presets as the Android app.</summary>
    public static readonly IReadOnlyList<CommunityList> Presets =
    [
        new("hagezi-normal", "HaGeZi Multi Normal", "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/multi.txt",
            "Recommended. About 180,000 ad, tracker and telemetry domains.", Enabled: true, Custom: false),
        new("hagezi-light", "HaGeZi Multi Light", "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/light.txt",
            "A smaller list with very few false positives.", Enabled: false, Custom: false),
        new("adguard-dns", "AdGuard DNS filter", "https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt",
            "Combines EasyList, EasyPrivacy and AdGuard's mobile ads filter.", Enabled: false, Custom: false),
        new("stevenblack", "StevenBlack Unified hosts", "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts",
            "Popular hosts file covering ads and malware.", Enabled: false, Custom: false),
    ];

    public static IReadOnlyList<BuiltInListInfo> BuiltIn { get; } = LoadBuiltInIndex();

    public static bool IsEnabled(AppConfig config, BuiltInListInfo list) =>
        config.Lists.TryGetValue(list.Id, out var enabled) ? enabled : list.EnabledByDefault;

    public static IReadOnlyList<CommunityList> CommunityLists(AppConfig config)
    {
        var settings = config.CommunityLists.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var lists = Presets
            .Select(p => settings.TryGetValue(p.Id, out var s) ? p with { Enabled = s.Enabled } : p)
            .ToList();
        lists.AddRange(config.CommunityLists
            .Where(s => s.Url is not null && IsValidId(s.Id) && !Presets.Any(p => p.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new CommunityList(s.Id, s.Title ?? s.Id, s.Url!, "Added by you", s.Enabled, Custom: true)));
        return lists;
    }

    /// <summary>List ids become file names, so only simple ids are accepted.</summary>
    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public string CachedFile(string id) =>
        IsValidId(id) ? Path.Combine(paths.ListsDirectory, $"{id}.txt") : throw new ArgumentException($"Invalid list id \"{id}\".", nameof(id));

    public DomainMatcher BuildMatcher(AppConfig config)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var list in BuiltIn.Where(l => IsEnabled(config, l)))
        {
            using var reader = OpenBuiltIn(list.Id);
            RuleParser.ParseInto(reader, blocked, allowed);
        }
        foreach (var list in CommunityLists(config).Where(l => l.Enabled))
        {
            var file = CachedFile(list.Id);
            if (!File.Exists(file)) continue;
            using var reader = File.OpenText(file);
            RuleParser.ParseInto(reader, blocked, allowed);
        }

        blocked.Add(FirefoxDohCanary);
        allowed.Remove(FirefoxDohCanary);

        // The user's own rules win over everything else.
        foreach (var domain in NormalizeAll(config.BlockedDomains))
        {
            blocked.Add(domain);
            allowed.Remove(domain);
        }
        foreach (var domain in NormalizeAll(config.AllowedDomains)) allowed.Add(domain);
        return new DomainMatcher(blocked, allowed);
    }

    /// <summary>Which enabled lists (or the user's own rules) contain <paramref name="rule"/>.</summary>
    public IReadOnlyList<string> FindSources(AppConfig config, string rule, bool allow)
    {
        var sources = new List<string>();
        if ((allow ? config.AllowedDomains : config.BlockedDomains).Contains(rule, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(allow ? "your allowed domains" : "your blocked domains");
        }
        if (!allow && rule == FirefoxDohCanary) sources.Add("AdBlocker (keeps Firefox on system DNS)");

        foreach (var list in BuiltIn.Where(l => IsEnabled(config, l)))
        {
            using var reader = OpenBuiltIn(list.Id);
            if (Contains(reader, rule, allow)) sources.Add($"{list.Title} (built-in)");
        }
        foreach (var list in CommunityLists(config).Where(l => l.Enabled && File.Exists(CachedFile(l.Id))))
        {
            using var reader = File.OpenText(CachedFile(list.Id));
            if (Contains(reader, rule, allow)) sources.Add(list.Title);
        }
        return sources;
    }

    public Dictionary<string, ListStatus> LoadStatus()
    {
        try
        {
            return File.Exists(paths.ListStatusFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(paths.ListStatusFile), AppJson.Default.DictionaryStringListStatus) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Downloads every enabled community list that is missing or older than <see cref="MaxAge"/>.</summary>
    /// <returns>True if any list file changed.</returns>
    public async Task<bool> RefreshStaleAsync(AppConfig config, HttpClient http, Action<string>? report, CancellationToken cancellationToken)
    {
        var status = LoadStatus();
        var changed = false;
        foreach (var list in CommunityLists(config).Where(l => l.Enabled))
        {
            var fresh = status.TryGetValue(list.Id, out var s) && s.UpdatedAt > DateTimeOffset.UtcNow - MaxAge && File.Exists(CachedFile(list.Id));
            if (fresh) continue;
            var result = await DownloadAsync(list, http, cancellationToken);
            report?.Invoke(result.Error is null
                ? $"Downloaded {list.Title}: {result.Domains:N0} domains"
                : $"Could not download {list.Title}: {result.Error}");
            changed |= result.Error is null;
        }
        return changed;
    }

    public async Task<ListStatus> DownloadAsync(CommunityList list, HttpClient http, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ListsDirectory);
        var temp = CachedFile(list.Id) + ".download";
        var status = LoadStatus().GetValueOrDefault(list.Id) ?? new ListStatus();
        try
        {
            if (!Uri.TryCreate(list.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("Only https:// addresses are allowed.");
            }
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new InvalidDataException("The list is larger than 50 MB.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(temp);
                var buffer = new byte[81_920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) throw new InvalidDataException("The list is larger than 50 MB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var blocked = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = File.OpenText(temp))
            {
                RuleParser.ParseInto(reader, blocked, new HashSet<string>(StringComparer.Ordinal));
            }
            if (blocked.Count == 0) throw new InvalidDataException("No domains found. Is this a blocklist?");

            File.Move(temp, CachedFile(list.Id), overwrite: true);
            status = new ListStatus { UpdatedAt = DateTimeOffset.UtcNow, Domains = blocked.Count };
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                      or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            File.Delete(temp);
            status.Error = e is TaskCanceledException ? "The download timed out." : e.Message;
        }
        SaveStatus(list.Id, status);
        return status;
    }

    public void DeleteCached(string id)
    {
        File.Delete(CachedFile(id));
        var status = LoadStatus();
        if (status.Remove(id)) WriteStatus(status);
    }

    public static int CountDomains(string id)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        using var reader = OpenBuiltIn(id);
        RuleParser.ParseInto(reader, blocked, new HashSet<string>(StringComparer.Ordinal));
        return blocked.Count;
    }

    public static HttpClient CreateDownloadClient() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    })
    {
        Timeout = TimeSpan.FromMinutes(3),
        DefaultRequestHeaders = { { "User-Agent", $"AdBlocker-Windows/{AppInfo.Version}" } },
    };

    private void SaveStatus(string id, ListStatus status)
    {
        var all = LoadStatus();
        all[id] = status;
        WriteStatus(all);
    }

    private void WriteStatus(Dictionary<string, ListStatus> all) =>
        AtomicFile.WriteAllText(paths.ListStatusFile, JsonSerializer.Serialize(all, AppJson.Default.DictionaryStringListStatus));

    private static IEnumerable<string> NormalizeAll(IEnumerable<string> domains) =>
        domains.Select(RuleParser.NormalizeDomain).OfType<string>();

    private static bool Contains(TextReader reader, string rule, bool allow)
    {
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (!line.Contains(rule, StringComparison.OrdinalIgnoreCase)) continue;
            if (RuleParser.ParseLine(line).Any(r => r.Allow == allow && r.Domain == rule)) return true;
        }
        return false;
    }

    private static TextReader OpenBuiltIn(string id)
    {
        var stream = typeof(FilterLists).Assembly.GetManifestResourceStream($"filters/{id}.txt")
            ?? throw new InvalidOperationException($"Built-in list {id} is missing from the executable.");
        return new StreamReader(stream);
    }

    private static List<BuiltInListInfo> LoadBuiltInIndex()
    {
        using var stream = typeof(FilterLists).Assembly.GetManifestResourceStream("filters/lists.json")
            ?? throw new InvalidOperationException("filters/lists.json is missing from the executable.");
        return JsonSerializer.Deserialize(stream, AppJson.Default.ListBuiltInListInfo) ?? [];
    }
}
