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

/// <summary>An entry of filters/community-lists.json, the presets both apps offer.</summary>
public sealed class CommunityListInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
    public bool EnabledByDefault { get; set; }
    public bool Scam { get; set; }
}

/// <param name="Scam">
/// A list of scam and fraud sites. Domains from these are worth warning about, because a
/// blocked shop or "free trial" looks like a broken website rather than a lucky escape.
/// </param>
public sealed record CommunityList(string Id, string Title, string Url, string Description, bool Enabled, bool Custom, bool Scam = false);

public sealed class ListStatus
{
    public DateTimeOffset? UpdatedAt { get; set; }
    public int Domains { get; set; }
    public string? Error { get; set; }
}

/// <summary>Why a domain is blocked or not (see <see cref="FilterLists.Explain"/>).</summary>
/// <param name="Domain">The name that was checked, lower case without a trailing dot.</param>
/// <param name="Decision">What happens to a lookup right now, a pause and temporary allowances included.</param>
/// <param name="Lasting">What the rules say once any pause and temporary allowance are over.</param>
/// <param name="Sources">Where the rule of <paramref name="Lasting"/> comes from: the user's rules, AdBlocker itself or list titles.</param>
/// <param name="Scam">Blocked right now and on one of the scam lists, so the blocker would warn about it.</param>
public sealed record Explanation(string Domain, Decision Decision, Decision Lasting, IReadOnlyList<string> Sources, bool Scam);

/// <summary>
/// The built-in lists (embedded from the repository's filters/ folder), the optional
/// community lists (downloaded on request), and the user's own rules.
/// </summary>
public sealed class FilterLists(AppPaths paths)
{
    /// <summary>Tells Firefox not to switch to its own DNS-over-HTTPS, which would skip the filter.</summary>
    public const string FirefoxDohCanary = "use-application-dns.net";

    /// <summary>
    /// Tell iPhones, iPads and Macs that this network does not allow iCloud Private Relay, which
    /// would send Safari's lookups past AdBlocker. The device then offers to turn it off here.
    /// </summary>
    public static readonly IReadOnlyList<string> PrivateRelayCanaries = ["mask.icloud.com", "mask-h2.icloud.com"];

    /// <summary>Every name AdBlocker blocks to keep browsers and devices on this DNS, unless the user allows it.</summary>
    public static readonly IReadOnlyList<string> Canaries = [FirefoxDohCanary, .. PrivateRelayCanaries];

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private const long MaxDownloadBytes = 50 * 1024 * 1024;

    /// <summary>The community list presets, from filters/community-lists.json (shared with the Android app).</summary>
    public static IReadOnlyList<CommunityList> Presets { get; } = LoadPresets();

    public static IReadOnlyList<BuiltInListInfo> BuiltIn { get; } = LoadBuiltInIndex();

    public static bool IsEnabled(AppConfig config, BuiltInListInfo list) =>
        config.Lists.TryGetValue(list.Id, out var enabled) ? enabled : list.EnabledByDefault;

    public static IReadOnlyList<CommunityList> CommunityLists(AppConfig config)
    {
        // A hand-edited config.json can name a list twice; the last entry wins.
        var settings = new Dictionary<string, CommunityListSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in config.CommunityLists)
        {
            if (setting is not null && !string.IsNullOrWhiteSpace(setting.Id)) settings[setting.Id] = setting;
        }
        var lists = Presets
            .Select(p => settings.TryGetValue(p.Id, out var s) ? p with { Enabled = s.Enabled } : p)
            .ToList();
        lists.AddRange(settings.Values
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
            using var reader = ReadIntoMemory(file);
            RuleParser.ParseInto(reader, blocked, allowed);
        }

        // Kept apart from the lists: the user's own rules decide first, then the canaries.
        return new DomainMatcher(blocked, allowed, NormalizeAll(config.BlockedDomains), NormalizeAll(config.AllowedDomains), Canaries);
    }

    /// <summary>
    /// Only the domains from the enabled scam lists, so a blocked lookup can be reported as
    /// "this site is a known scam" instead of just failing. It only says what the scam lists
    /// hold; whether a name is blocked at all is up to <see cref="BuildMatcher"/>, see
    /// <see cref="RuleSet.IsScam"/>.
    /// </summary>
    public DomainMatcher BuildScamMatcher(AppConfig config)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in CommunityLists(config).Where(l => l is { Enabled: true, Scam: true }))
        {
            var file = CachedFile(list.Id);
            if (!File.Exists(file)) continue;
            using var reader = ReadIntoMemory(file);
            RuleParser.ParseInto(reader, blocked, allowed);
        }
        return new DomainMatcher(blocked, allowed);
    }

    /// <summary>
    /// What the big matchers are built from. While it stays the same, a change to config.json
    /// (a pause, an allowance, another DNS server) can keep the matchers already built.
    /// </summary>
    public static string MatcherInputs(AppConfig config) => string.Join('\n',
        string.Join(' ', BuiltIn.Where(l => IsEnabled(config, l)).Select(l => l.Id)),
        string.Join(' ', CommunityLists(config).Where(l => l.Enabled).Select(l => $"{l.Id}={l.Url}")),
        string.Join(' ', NormalizeAll(config.BlockedDomains).Order(StringComparer.Ordinal)),
        string.Join(' ', NormalizeAll(config.AllowedDomains).Order(StringComparer.Ordinal)));

    /// <summary>
    /// Why <paramref name="domain"/> is blocked or not, worked out from the configuration alone,
    /// so "adblocker check" answers like the running blocker without having to ask it.
    /// </summary>
    public Explanation Explain(AppConfig config, string domain, DateTimeOffset now)
    {
        var name = DomainMatcher.Normalize(domain);
        var rules = new RuleSet(BuildMatcher(config), BuildScamMatcher(config), new TemporaryAllowances(config.TemporarilyAllowed), config.PausedUntil);
        var lasting = rules.Matcher.Decide(name, TemporaryAllowances.None, now);
        IReadOnlyList<string> sources = lasting switch
        {
            { Rule: null } => [],
            { Kind: RuleKind.UserAllow } => [YourAllowed],
            { Kind: RuleKind.UserBlock } => [YourBlocked],
            { Kind: RuleKind.Canary, Rule: var rule } => [CanaryLabel(rule)],
            { Rule: var rule } => FindListSources(config, rule, allow: !lasting.Blocked),
        };
        return new Explanation(name, rules.Decide(name, now), lasting, sources, rules.IsScam(name, now));
    }

    /// <summary>Which enabled lists (or the user's own rules, or AdBlocker itself) contain <paramref name="rule"/>.</summary>
    public IReadOnlyList<string> FindSources(AppConfig config, string rule, bool allow)
    {
        var sources = new List<string>();
        if ((allow ? config.AllowedDomains : config.BlockedDomains).Contains(rule, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(allow ? YourAllowed : YourBlocked);
        }
        if (!allow && Canaries.Contains(rule)) sources.Add(CanaryLabel(rule));
        sources.AddRange(FindListSources(config, rule, allow));
        return sources;
    }

    private const string YourAllowed = "your allowed domains";
    private const string YourBlocked = "your blocked domains";

    private static string CanaryLabel(string rule) => rule == FirefoxDohCanary
        ? "AdBlocker (keeps Firefox on system DNS)"
        : "AdBlocker (iCloud Private Relay check)";

    /// <summary>Which enabled lists contain <paramref name="rule"/>.</summary>
    private List<string> FindListSources(AppConfig config, string rule, bool allow)
    {
        var sources = new List<string>();
        foreach (var list in BuiltIn.Where(l => IsEnabled(config, l)))
        {
            using var reader = OpenBuiltIn(list.Id);
            if (Contains(reader, rule, allow)) sources.Add($"{list.Title} (built-in)");
        }
        foreach (var list in CommunityLists(config).Where(l => l.Enabled && File.Exists(CachedFile(l.Id))))
        {
            using var reader = ReadIntoMemory(CachedFile(list.Id));
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

            // The running blocker or a "check" may be reading the old copy. They hold it open only
            // for a moment (see ReadIntoMemory), and Replace retries for about that long.
            AtomicFile.Replace(temp, CachedFile(list.Id));
            status = new ListStatus { UpdatedAt = DateTimeOffset.UtcNow, Domains = blocked.Count };
        }
        // Timeouts are OperationCanceledExceptions too; only the caller's token means "stop".
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                      or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            File.Delete(temp);
            status.Error = e is OperationCanceledException ? "The download timed out." : e.Message;
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

    /// <summary>
    /// Reads a list file into memory and closes it again at once. Windows can't replace a file
    /// while anyone has it open, however it is shared, so a newer download only gets in because
    /// <see cref="AtomicFile.Replace"/> retries for a moment: long enough for a read, not for
    /// parsing a big list.
    /// </summary>
    internal static StreamReader ReadIntoMemory(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var memory = new MemoryStream((int)Math.Min(stream.Length, Array.MaxLength));
        stream.CopyTo(memory);
        memory.Position = 0;
        return new StreamReader(memory);
    }

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

    private static List<CommunityList> LoadPresets()
    {
        using var stream = typeof(FilterLists).Assembly.GetManifestResourceStream("filters/community-lists.json")
            ?? throw new InvalidOperationException("filters/community-lists.json is missing from the executable.");
        var entries = JsonSerializer.Deserialize(stream, AppJson.Default.ListCommunityListInfo) ?? [];
        return entries
            .Select(e => new CommunityList(e.Id, e.Title, e.Url, e.Description, e.EnabledByDefault, Custom: false, e.Scam))
            .ToList();
    }

    private static List<BuiltInListInfo> LoadBuiltInIndex()
    {
        using var stream = typeof(FilterLists).Assembly.GetManifestResourceStream("filters/lists.json")
            ?? throw new InvalidOperationException("filters/lists.json is missing from the executable.");
        return JsonSerializer.Deserialize(stream, AppJson.Default.ListBuiltInListInfo) ?? [];
    }
}
