using AdBlocker.Filtering;
using static AdBlocker.Filtering.RuleParser;

namespace AdBlocker.Tests;

public class RuleParserTests
{
    private static Rule[] Block(params string[] domains) => domains.Select(d => new Rule(d, Allow: false)).ToArray();

    [Theory]
    [InlineData("0.0.0.0 ads.example.com")]
    [InlineData("127.0.0.1\tAds.Example.com # comment")]
    [InlineData(":: ads.example.com")]
    [InlineData("ads.example.com")]
    [InlineData("  ads.example.com.  ")]
    [InlineData("||ads.example.com^")]
    [InlineData("||ads.example.com^$important")]
    [InlineData("||*.ads.example.com^")]
    [InlineData("*.ads.example.com")]
    public void Reads_blocking_rules(string line) => Assert.Equal(Block("ads.example.com"), ParseLine(line));

    [Fact]
    public void Reads_several_hosts_on_one_line() =>
        Assert.Equal(Block("a.example.com", "b.example.com"), ParseLine("0.0.0.0 a.example.com b.example.com"));

    [Theory]
    [InlineData("@@||cdn.example.com^")]
    [InlineData("@@||cdn.example.com^|")]
    [InlineData("@@cdn.example.com")]
    public void Reads_exceptions(string line) => Assert.Equal(new[] { new Rule("cdn.example.com", Allow: true) }, ParseLine(line));

    [Theory]
    [InlineData("||ads.example.com^$third-party")]
    [InlineData("||ads.example.com^$important,dnstype=AAAA")]
    [InlineData("||example.com/ads/")]
    [InlineData("||ads.example")]
    [InlineData("||ads.example.com")]
    [InlineData("|https://ads.example.com^")]
    [InlineData(@"/banner\d+/")]
    [InlineData("ads.example.com^")]
    [InlineData("example.com##.ad-banner")]
    [InlineData("example.com#@#.ad-banner")]
    [InlineData("example.com#?#div:has-text(Ad)")]
    [InlineData("example.com#$#abort-on-property-read x")]
    [InlineData("##.ad")]
    [InlineData("! comment")]
    [InlineData("# comment")]
    [InlineData("[Adblock Plus 2.0]")]
    [InlineData("")]
    [InlineData("com")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1 localhost")]
    [InlineData("::1 ip6-localhost ip6-loopback")]
    [InlineData("255.255.255.255 broadcasthost")]
    [InlineData("0.0.0.0 0.0.0.0")]
    [InlineData("192.168.1.1")]
    [InlineData("example.com ads.example.com")]
    [InlineData("-bad.example.com")]
    [InlineData("bad-.example.com")]
    [InlineData("||example.*^")]
    public void Skips_what_DNS_cannot_express(string line) => Assert.Empty(ParseLine(line));

    [Fact]
    public void Normalizes_domains()
    {
        Assert.Equal("example.com", NormalizeDomain("Example.COM."));
        Assert.Equal("_dmarc.example.com", NormalizeDomain("_dmarc.example.com"));
        Assert.Equal("xn--bcher-kva.example", NormalizeDomain("xn--bcher-kva.example"));
        Assert.Null(NormalizeDomain("bücher.example"));
        Assert.Null(NormalizeDomain(new string('a', 64) + ".com"));
        Assert.Null(NormalizeDomain("a..com"));
        Assert.Null(NormalizeDomain("1.2.3.4"));
    }

    [Fact]
    public void Collects_rules_into_sets()
    {
        var blocked = new HashSet<string>();
        var allowed = new HashSet<string>();
        ParseInto(new StringReader("# list\n||a.com^\n0.0.0.0 b.com\n@@||c.com^\na.com\n||d.com^$3p\n"), blocked, allowed);
        Assert.Equal(new[] { "a.com", "b.com" }, blocked.Order());
        Assert.Equal(new[] { "c.com" }, allowed);
    }

    [Fact]
    public void Every_shared_list_uses_the_expected_format_without_duplicates()
    {
        var directory = Path.Combine(TestPaths.RepositoryRoot, "filters");
        var files = Directory.GetFiles(directory, "*.txt");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var rules = File.ReadAllLines(file).Where(l => l.Length > 0 && !l.StartsWith('!')).ToList();
            Assert.All(rules, rule => Assert.Matches(@"^(@@)?\|\|[a-z0-9.-]+\^$", rule));

            var blocked = new HashSet<string>();
            var allowed = new HashSet<string>();
            using var reader = File.OpenText(file);
            ParseInto(reader, blocked, allowed);
            Assert.Equal(rules.Count, blocked.Count + allowed.Count);
        }
    }
}
