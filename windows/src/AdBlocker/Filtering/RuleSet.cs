namespace AdBlocker.Filtering;

/// <summary>
/// Everything that decides whether a lookup is blocked right now: the lists with the user's own
/// rules, the temporary allowances and a pause. It never changes, so the blocker can swap in a
/// new one in a single step and a lookup never sees half of a change.
/// </summary>
/// <param name="scam">Only the scam lists, to tell a blocked scam site from an ordinary ad server.</param>
/// <param name="pausedUntil">When "adblocker pause" ends; earlier times mean blocking.</param>
public sealed class RuleSet(DomainMatcher matcher, DomainMatcher scam, TemporaryAllowances allowances, DateTimeOffset? pausedUntil)
{
    public static RuleSet Empty { get; } = new(DomainMatcher.Empty, DomainMatcher.Empty, TemporaryAllowances.None, null);

    public DomainMatcher Matcher => matcher;

    public DomainMatcher Scam => scam;

    public TemporaryAllowances Allowances => allowances;

    /// <summary>When the pause ends, or null when blocking at <paramref name="now"/>.</summary>
    public DateTimeOffset? PausedUntil(DateTimeOffset now) => pausedUntil is { } until && until > now ? until : null;

    /// <summary>Whether <paramref name="name"/> is blocked at <paramref name="now"/>, and why.</summary>
    public Decision Decide(string name, DateTimeOffset now)
    {
        var decision = matcher.Decide(name, allowances, now);
        if (PausedUntil(now) is not { } until) return decision;
        return decision.Blocked && matcher.IsCanary(DomainMatcher.Normalize(name))
            ? decision
            : new Decision(false, null, RuleKind.Paused, until);
    }

    /// <summary>Like <see cref="Decide"/>, but without allocating; <paramref name="name"/> is lower case without a trailing dot.</summary>
    public bool IsBlocked(string name, DateTimeOffset now)
    {
        if (!matcher.IsBlocked(name, allowances, now)) return false;
        // A pause lets everything through except the checks that keep browsers and phones on
        // this DNS (Firefox's DNS over HTTPS, iCloud Private Relay). Once they fail them, they
        // keep going around AdBlocker long after the pause is over.
        return PausedUntil(now) is null || matcher.IsCanary(name);
    }

    /// <summary>Whether <paramref name="name"/> is blocked right now and on one of the scam lists, so worth a warning.</summary>
    public bool IsScam(string name, DateTimeOffset now) => IsBlocked(name, now) && scam.IsBlocked(name);

    /// <summary>Whether the pause ran out after <paramref name="since"/> and by <paramref name="now"/>.</summary>
    public bool PauseEndedBetween(DateTimeOffset since, DateTimeOffset now) => pausedUntil is { } until && until > since && until <= now;
}
