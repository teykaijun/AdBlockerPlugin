namespace AdBlocker.Filtering;

/// <summary>
/// Domains allowed for a while ("adblocker allow example.com --for 10m"). Checked on every
/// lookup, so an entry takes effect and expires without rebuilding the big lists. Like a
/// permanent allow, it also covers subdomains, and it counts as one of the user's own rules
/// (see <see cref="DomainMatcher"/>).
/// </summary>
public sealed class TemporaryAllowances
{
    public static readonly TemporaryAllowances None = new(null);

    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public TemporaryAllowances(IReadOnlyDictionary<string, DateTimeOffset>? until)
    {
        foreach (var (domain, time) in until ?? new Dictionary<string, DateTimeOffset>())
        {
            if (RuleParser.NormalizeDomain(domain) is not { } name) continue;
            if (!_until.TryGetValue(name, out var other) || time > other) _until[name] = time;
        }
        _lookup = _until.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public bool IsEmpty => _until.Count == 0;

    /// <summary>Whether <paramref name="name"/> or one of its parent domains is allowed at <paramref name="now"/>.</summary>
    public bool Allows(string name, DateTimeOffset now)
    {
        if (_until.Count == 0) return false;
        for (var host = DomainMatcher.Normalize(name).AsSpan(); !host.IsEmpty;)
        {
            if (TryGetActive(host, now, out _)) return true;
            var dot = host.IndexOf('.');
            if (dot < 0) return false;
            host = host[(dot + 1)..];
        }
        return false;
    }

    /// <summary>The allowance for exactly <paramref name="domain"/> (lower case), if it is still running at <paramref name="now"/>.</summary>
    public bool TryGetActive(ReadOnlySpan<char> domain, DateTimeOffset now, out DateTimeOffset until)
    {
        if (_until.Count > 0 && _lookup.TryGetValue(domain, out until) && until > now) return true;
        until = default;
        return false;
    }

    /// <summary>Entries that have not expired yet, soonest first.</summary>
    public IReadOnlyList<(string Domain, DateTimeOffset Until)> Active(DateTimeOffset now) =>
        _until.Where(e => e.Value > now).OrderBy(e => e.Value).Select(e => (e.Key, e.Value)).ToList();

    /// <summary>The domains whose allowance ran out after <paramref name="since"/> and by <paramref name="now"/>.</summary>
    public IReadOnlyList<string> EndedBetween(DateTimeOffset since, DateTimeOffset now) =>
        _until.Where(e => e.Value > since && e.Value <= now).OrderBy(e => e.Value).Select(e => e.Key).ToList();

    /// <summary>
    /// A copy of <paramref name="until"/> without the entries that have run out (or are not domains),
    /// with every domain in the form the rules use.
    /// </summary>
    public static Dictionary<string, DateTimeOffset> Prune(IReadOnlyDictionary<string, DateTimeOffset>? until, DateTimeOffset now)
    {
        var pruned = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (domain, time) in until ?? new Dictionary<string, DateTimeOffset>())
        {
            if (time <= now || RuleParser.NormalizeDomain(domain) is not { } name) continue;
            if (!pruned.TryGetValue(name, out var other) || time > other) pruned[name] = time;
        }
        return pruned;
    }
}
