using AdBlocker.Filtering;

namespace AdBlocker.Config;

/// <summary>Changes to the user's own rules, in one place so every command makes them the same way.</summary>
public static class ConfigRules
{
    /// <summary>
    /// Always allows (or always blocks) <paramref name="domain"/> and its subdomains. A temporary
    /// allowance for exactly that domain ends, so the new rule applies at once, and allowances
    /// that ran out are forgotten.
    /// </summary>
    public static void SetRule(AppConfig config, string domain, bool allow, DateTimeOffset now)
    {
        var name = Normalize(domain);
        config.BlockedDomains.RemoveAll(d => Same(d, name));
        config.AllowedDomains.RemoveAll(d => Same(d, name));
        (allow ? config.AllowedDomains : config.BlockedDomains).Add(name);
        config.AllowedDomains.Sort(StringComparer.Ordinal);
        config.BlockedDomains.Sort(StringComparer.Ordinal);

        var temporary = TemporaryAllowances.Prune(config.TemporarilyAllowed, now);
        temporary.Remove(name);
        config.TemporarilyAllowed = temporary;
    }

    /// <summary>Allows <paramref name="domain"/> and its subdomains until <paramref name="until"/>, and forgets allowances that ran out.</summary>
    public static void AllowFor(AppConfig config, string domain, DateTimeOffset until, DateTimeOffset now)
    {
        var temporary = TemporaryAllowances.Prune(config.TemporarilyAllowed, now);
        temporary[Normalize(domain)] = until;
        config.TemporarilyAllowed = temporary;
    }

    /// <summary>Removes the user's own rules for exactly <paramref name="domain"/>, a temporary allowance included. Returns how many there were.</summary>
    public static int Forget(AppConfig config, string domain, DateTimeOffset now)
    {
        var name = RuleParser.NormalizeDomain(domain) ?? domain.Trim().ToLowerInvariant();
        var removed = config.BlockedDomains.RemoveAll(d => Same(d, name)) + config.AllowedDomains.RemoveAll(d => Same(d, name));
        var temporary = TemporaryAllowances.Prune(config.TemporarilyAllowed, now);
        if (temporary.Remove(name)) removed++;
        config.TemporarilyAllowed = temporary;
        return removed;
    }

    private static string Normalize(string domain) =>
        RuleParser.NormalizeDomain(domain) ?? throw new ArgumentException($"\"{domain}\" is not a domain.", nameof(domain));

    /// <summary>Hand-edited entries may differ in case or end in a dot.</summary>
    private static bool Same(string entry, string name) =>
        string.Equals(RuleParser.NormalizeDomain(entry) ?? entry.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
