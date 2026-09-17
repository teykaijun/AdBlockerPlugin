namespace AdBlocker.Filtering;

public readonly record struct MatchResult(bool Blocked, string? Rule);

/// <summary>
/// Decides whether a hostname is blocked. A rule for <c>example.com</c> also covers
/// every subdomain, and an allow rule anywhere up the chain wins over a block rule,
/// the same as <c>@@||example.com^</c> in Adblock Plus syntax.
/// </summary>
public sealed class DomainMatcher
{
    public static DomainMatcher Empty { get; } = new([], []);

    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _blocked;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _allowed;

    public DomainMatcher(IEnumerable<string> blocked, IEnumerable<string> allowed)
    {
        var blockedSet = blocked as HashSet<string> is { } b && b.Comparer == StringComparer.Ordinal ? b : new HashSet<string>(blocked, StringComparer.Ordinal);
        var allowedSet = allowed as HashSet<string> is { } a && a.Comparer == StringComparer.Ordinal ? a : new HashSet<string>(allowed, StringComparer.Ordinal);
        _blocked = blockedSet.GetAlternateLookup<ReadOnlySpan<char>>();
        _allowed = allowedSet.GetAlternateLookup<ReadOnlySpan<char>>();
        BlockedCount = blockedSet.Count;
    }

    public int BlockedCount { get; }

    /// <param name="name">A lower-case name without a trailing dot, as <see cref="Dns.DnsMessage"/> produces.</param>
    public bool IsBlocked(ReadOnlySpan<char> name)
    {
        var hit = false;
        for (var host = name; !host.IsEmpty;)
        {
            if (_allowed.Contains(host)) return false;
            hit = hit || _blocked.Contains(host);
            var dot = host.IndexOf('.');
            if (dot < 0) break;
            host = host[(dot + 1)..];
        }
        return hit;
    }

    /// <summary>Like <see cref="IsBlocked"/>, but also reports which rule decided.</summary>
    public MatchResult Match(string domain)
    {
        string? blockedBy = null;
        for (var host = domain.TrimEnd('.').ToLowerInvariant().AsSpan(); !host.IsEmpty;)
        {
            if (_allowed.Contains(host)) return new MatchResult(false, host.ToString());
            if (blockedBy is null && _blocked.Contains(host)) blockedBy = host.ToString();
            var dot = host.IndexOf('.');
            if (dot < 0) break;
            host = host[(dot + 1)..];
        }
        return new MatchResult(blockedBy is not null, blockedBy);
    }
}
