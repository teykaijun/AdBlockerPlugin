namespace AdBlocker.Filtering;

/// <summary>
/// Reads the domain rules a DNS blocker can use from the common list formats:
/// <list type="bullet">
///   <item>hosts files: <c>0.0.0.0 ads.example.com tracker.example.net</c></item>
///   <item>plain domain lists: <c>ads.example.com</c></item>
///   <item>Adblock-style lists: <c>||ads.example.com^</c>, <c>@@||cdn.example.com^</c>, <c>||x.com^$important</c></item>
/// </list>
/// Anything that needs more than a hostname to decide (paths, <c>$third-party</c>,
/// element hiding, regular expressions) is skipped. Mirrors the Android app's RuleParser.
/// </summary>
public static class RuleParser
{
    public readonly record struct Rule(string Domain, bool Allow);

    /// <summary>Hostnames that hosts files map for the local machine, never ad servers.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "localhost", "localhost.localdomain", "local", "broadcasthost",
        "ip6-localhost", "ip6-loopback", "ip6-localnet", "ip6-mcastprefix",
        "ip6-allnodes", "ip6-allrouters", "ip6-allhosts",
    };

    public static IReadOnlyList<Rule> ParseLine(string raw)
    {
        var rules = new List<Rule>(1);
        ParseLine(raw, rules);
        return rules;
    }

    public static void ParseInto(TextReader reader, ISet<string> blocked, ISet<string> allowed)
    {
        var rules = new List<Rule>(4);
        for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            rules.Clear();
            ParseLine(line, rules);
            foreach (var rule in rules)
            {
                (rule.Allow ? allowed : blocked).Add(rule.Domain);
            }
        }
    }

    private static void ParseLine(string raw, List<Rule> output)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed[0] is '!' or '[' || IsCosmetic(trimmed)) return;

        var hash = trimmed.IndexOf('#');
        var line = (hash >= 0 ? trimmed[..hash] : trimmed).Trim();
        if (line.Length == 0) return;

        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            if (!IsAddress(parts[0])) return;
            foreach (var host in parts.AsSpan(1))
            {
                if (NormalizeDomain(host) is { } domain) output.Add(new Rule(domain, Allow: false));
            }
            return;
        }

        var token = parts[0];
        var allow = token.StartsWith("@@", StringComparison.Ordinal);
        if (allow) token = token[2..];

        var dollar = token.IndexOf('$');
        if (dollar >= 0)
        {
            // $important is the only option that still means something for DNS.
            foreach (var option in token[(dollar + 1)..].Split(','))
            {
                if (option != "important") return;
            }
            token = token[..dollar];
        }

        if (token.StartsWith("||", StringComparison.Ordinal))
        {
            token = token[2..];
            if (token.EndsWith('|')) token = token[..^1];
            if (!token.EndsWith('^')) return;
            token = token[..^1];
        }
        else if (token.StartsWith('|') || token.EndsWith('^'))
        {
            return;
        }
        if (token.StartsWith("*.", StringComparison.Ordinal)) token = token[2..];

        if (NormalizeDomain(token) is { } normalized) output.Add(new Rule(normalized, allow));
    }

    /// <summary>Lower-cased hostname with at least two labels, or null if <paramref name="input"/> is not one.</summary>
    public static string? NormalizeDomain(string input)
    {
        var domain = input.Trim().TrimEnd('.').ToLowerInvariant();
        if (Ignored.Contains(domain) || !IsHostname(domain) || IsIPv4(domain)) return null;
        return domain;
    }

    // Hand-written instead of a regex: community lists have hundreds of
    // thousands of lines and this runs for each of them.
    private static bool IsHostname(string name)
    {
        if (name.Length is 0 or > 253) return false;
        var labels = 0;
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i == name.Length || name[i] == '.')
            {
                var length = i - start;
                if (length is 0 or > 63 || name[start] == '-' || name[i - 1] == '-') return false;
                labels++;
                start = i + 1;
            }
            else if (name[i] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }
        return labels >= 2;
    }

    private static bool IsIPv4(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit));
    }

    private static bool IsAddress(string token) => token == "0" || IsIPv4(token) || token.Contains(':');

    /// <summary>Element-hiding and scriptlet rules: <c>##</c>, <c>#@#</c>, <c>#?#</c>, <c>#$#</c>, <c>#%#</c>.</summary>
    private static bool IsCosmetic(string line)
    {
        for (var i = line.IndexOf('#'); i >= 0 && i < line.Length - 1; i = line.IndexOf('#', i + 1))
        {
            var next = line[i + 1];
            if (next == '#') return true;
            if (next is '@' or '?' or '$' or '%' && i + 2 < line.Length && line[i + 2] == '#') return true;
        }
        return false;
    }
}
