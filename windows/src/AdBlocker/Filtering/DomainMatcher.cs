using Lookup = System.Collections.Generic.HashSet<string>.AlternateLookup<System.ReadOnlySpan<char>>;

namespace AdBlocker.Filtering;

/// <summary>What decided whether a name is blocked.</summary>
public enum RuleKind
{
    /// <summary>No rule covers the name.</summary>
    None,

    /// <summary>Blocking is paused ("adblocker pause").</summary>
    Paused,

    /// <summary>The user's own allowed domains.</summary>
    UserAllow,

    /// <summary>The user's own blocked domains.</summary>
    UserBlock,

    /// <summary>Allowed for a while ("adblocker allow example.com --for 10m").</summary>
    TemporaryAllow,

    /// <summary>A check AdBlocker always fails, such as Firefox's DNS-over-HTTPS canary.</summary>
    Canary,

    /// <summary>An exception in a filter list (<c>@@||example.com^</c>).</summary>
    ListAllow,

    /// <summary>A filter list.</summary>
    ListBlock,
}

/// <param name="Rule">
/// The domain of the rule that decided: the name itself or one of its parents. Null for
/// <see cref="RuleKind.None"/> and <see cref="RuleKind.Paused"/>.
/// </param>
/// <param name="Until">When a temporary allowance or a pause ends.</param>
public readonly record struct Decision(bool Blocked, string? Rule, RuleKind Kind, DateTimeOffset? Until = null);

/// <summary>
/// Decides whether a hostname is blocked. A rule for <c>example.com</c> also covers every
/// subdomain. The user's own rules come first: the most specific level of the name that has
/// one decides, and when a level has both, allowing wins. Next come the checks AdBlocker always
/// fails. Only then do the lists decide: an exception anywhere up the chain wins over a block
/// rule, the same as <c>@@||example.com^</c> in Adblock Plus syntax.
/// </summary>
public sealed class DomainMatcher
{
    public static DomainMatcher Empty { get; } = new([], []);

    private readonly Lookup _blocked;
    private readonly Lookup _allowed;
    private readonly Lookup _userBlocked;
    private readonly Lookup _userAllowed;
    private readonly Lookup _canaries;

    /// <param name="blocked">Domains the lists block.</param>
    /// <param name="allowed">Exceptions in the lists.</param>
    /// <param name="userBlocked">The user's own blocked domains.</param>
    /// <param name="userAllowed">The user's own allowed domains.</param>
    /// <param name="canaries">Names AdBlocker always blocks unless the user allows them.</param>
    public DomainMatcher(
        IEnumerable<string> blocked,
        IEnumerable<string> allowed,
        IEnumerable<string>? userBlocked = null,
        IEnumerable<string>? userAllowed = null,
        IEnumerable<string>? canaries = null)
    {
        var blockedSet = ToSet(blocked);
        var userBlockedSet = ToSet(userBlocked ?? []);
        var canarySet = ToSet(canaries ?? []);
        _blocked = blockedSet.GetAlternateLookup<ReadOnlySpan<char>>();
        _allowed = ToSet(allowed).GetAlternateLookup<ReadOnlySpan<char>>();
        _userBlocked = userBlockedSet.GetAlternateLookup<ReadOnlySpan<char>>();
        _userAllowed = ToSet(userAllowed ?? []).GetAlternateLookup<ReadOnlySpan<char>>();
        _canaries = canarySet.GetAlternateLookup<ReadOnlySpan<char>>();
        BlockedCount = blockedSet.Count
                       + userBlockedSet.Count(d => !blockedSet.Contains(d))
                       + canarySet.Count(d => !blockedSet.Contains(d) && !userBlockedSet.Contains(d));
    }

    /// <summary>How many domains the lists, the user and AdBlocker itself block.</summary>
    public int BlockedCount { get; }

    /// <summary>Lower case without a trailing dot, the form every rule is kept in.</summary>
    public static string Normalize(string name) => name.TrimEnd('.').ToLowerInvariant();

    /// <param name="name">A lower-case name without a trailing dot, as <see cref="Dns.DnsMessage"/> produces.</param>
    public bool IsBlocked(ReadOnlySpan<char> name) => Blocks(Find(name, TemporaryAllowances.None, default, out _, out _));

    /// <summary>Like <see cref="IsBlocked(ReadOnlySpan{char})"/>, with the allowances running at <paramref name="now"/>.</summary>
    public bool IsBlocked(ReadOnlySpan<char> name, TemporaryAllowances allowances, DateTimeOffset now) =>
        Blocks(Find(name, allowances, now, out _, out _));

    /// <summary>Whether a check AdBlocker always fails covers <paramref name="name"/> (lower case, no trailing dot).</summary>
    public bool IsCanary(ReadOnlySpan<char> name)
    {
        for (var start = 0; start < name.Length; start = Next(name, start))
        {
            if (_canaries.Contains(name[start..])) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="name"/> is blocked, and which rule decided.</summary>
    public Decision Decide(string name, TemporaryAllowances allowances, DateTimeOffset now)
    {
        var key = Normalize(name);
        var kind = Find(key, allowances, now, out var start, out var until);
        return kind switch
        {
            RuleKind.None => new Decision(false, null, kind),
            RuleKind.TemporaryAllow => new Decision(false, key[start..], kind, until),
            _ => new Decision(Blocks(kind), key[start..], kind),
        };
    }

    /// <summary>Like <see cref="Decide"/>, without temporary allowances.</summary>
    public Decision Match(string domain) => Decide(domain, TemporaryAllowances.None, default);

    /// <param name="start">Where the level that decided starts in <paramref name="name"/>.</param>
    private RuleKind Find(ReadOnlySpan<char> name, TemporaryAllowances allowances, DateTimeOffset now, out int start, out DateTimeOffset until)
    {
        until = default;

        // The user's own rules: the most specific level that has one decides.
        for (start = 0; start < name.Length; start = Next(name, start))
        {
            var level = name[start..];
            if (_userAllowed.Contains(level)) return RuleKind.UserAllow;
            if (allowances.TryGetActive(level, now, out until)) return RuleKind.TemporaryAllow;
            if (_userBlocked.Contains(level)) return RuleKind.UserBlock;
        }

        // Kept apart from the lists, because no list exception may switch these off.
        for (start = 0; start < name.Length; start = Next(name, start))
        {
            if (_canaries.Contains(name[start..])) return RuleKind.Canary;
        }

        var blockedAt = -1;
        for (start = 0; start < name.Length; start = Next(name, start))
        {
            var level = name[start..];
            if (_allowed.Contains(level)) return RuleKind.ListAllow;
            if (blockedAt < 0 && _blocked.Contains(level)) blockedAt = start;
        }
        start = Math.Max(blockedAt, 0);
        return blockedAt >= 0 ? RuleKind.ListBlock : RuleKind.None;
    }

    /// <summary>Where the parent domain of the level at <paramref name="start"/> begins (the end when there is none).</summary>
    private static int Next(ReadOnlySpan<char> name, int start)
    {
        var dot = name[start..].IndexOf('.');
        return dot < 0 ? name.Length : start + dot + 1;
    }

    private static bool Blocks(RuleKind kind) => kind is RuleKind.UserBlock or RuleKind.Canary or RuleKind.ListBlock;

    private static HashSet<string> ToSet(IEnumerable<string> domains) =>
        domains is HashSet<string> set && set.Comparer == StringComparer.Ordinal ? set : new HashSet<string>(domains, StringComparer.Ordinal);
}
