using AdBlocker.Config;
using AdBlocker.Filtering;

namespace AdBlocker.Tests;

/// <summary>
/// The order every decision follows (the Android app's matcher does the same): a pause first,
/// then the most specific level with a rule of the user's own (allowing wins at a level),
/// then AdBlocker's canaries, then the lists.
/// </summary>
public class RulePrecedenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static DomainMatcher Matcher(
        string[]? blocked = null,
        string[]? allowed = null,
        string[]? userBlocked = null,
        string[]? userAllowed = null) =>
        new(blocked ?? [], allowed ?? [], userBlocked, userAllowed, FilterLists.Canaries);

    private static TemporaryAllowances Allow(string domain, DateTimeOffset until) =>
        new(new Dictionary<string, DateTimeOffset> { [domain] = until });

    [Fact]
    public void Your_block_beats_a_list_exception_for_a_parent()
    {
        var matcher = Matcher(allowed: ["example.com"], userBlocked: ["ads.example.com"]);
        Assert.Equal(new Decision(true, "ads.example.com", RuleKind.UserBlock), matcher.Match("x.ads.example.com"));
        Assert.Equal(new Decision(false, "example.com", RuleKind.ListAllow), matcher.Match("www.example.com"));
    }

    [Fact]
    public void Your_block_of_a_subdomain_beats_your_allow_of_its_parent()
    {
        var matcher = Matcher(userBlocked: ["ads.example.com"], userAllowed: ["example.com"]);
        Assert.True(matcher.IsBlocked("ads.example.com"));
        Assert.True(matcher.IsBlocked("img.ads.example.com"));
        Assert.False(matcher.IsBlocked("www.example.com"));
        Assert.Equal(new Decision(false, "example.com", RuleKind.UserAllow), matcher.Match("www.example.com"));
    }

    [Fact]
    public void Allowing_wins_when_one_level_has_both()
    {
        Assert.False(Matcher(userBlocked: ["shop.example"], userAllowed: ["shop.example"]).IsBlocked("shop.example"));
        var allowance = Allow("shop.example", Now.AddMinutes(10));
        Assert.False(Matcher(userBlocked: ["shop.example"]).IsBlocked("cdn.shop.example", allowance, Now));
    }

    [Fact]
    public void A_temporary_allow_lets_subdomains_through_a_list_block()
    {
        var decision = Matcher(blocked: ["cdn.shop.example"]).Decide("cdn.shop.example", Allow("shop.example", Now.AddMinutes(10)), Now);
        Assert.Equal(new Decision(false, "shop.example", RuleKind.TemporaryAllow, Now.AddMinutes(10)), decision);
    }

    [Fact]
    public void Your_block_of_a_subdomain_beats_a_temporary_allow_of_its_parent()
    {
        var matcher = Matcher(userBlocked: ["cdn.shop.example"]);
        Assert.True(matcher.IsBlocked("cdn.shop.example", Allow("shop.example", Now.AddMinutes(10)), Now));
        Assert.False(matcher.IsBlocked("www.shop.example", Allow("shop.example", Now.AddMinutes(10)), Now));
    }

    [Fact]
    public void A_temporary_allow_of_a_subdomain_beats_your_block_of_its_parent()
    {
        var matcher = Matcher(userBlocked: ["shop.example"]);
        Assert.False(matcher.IsBlocked("cdn.shop.example", Allow("cdn.shop.example", Now.AddMinutes(10)), Now));
        Assert.True(matcher.IsBlocked("www.shop.example", Allow("cdn.shop.example", Now.AddMinutes(10)), Now));
    }

    [Fact]
    public void Allowances_that_ran_out_do_nothing()
    {
        var matcher = Matcher(blocked: ["shop.example"]);
        var expired = Allow("shop.example", Now.AddSeconds(-1));
        Assert.Equal(new Decision(true, "shop.example", RuleKind.ListBlock), matcher.Decide("shop.example", expired, Now));
        Assert.True(matcher.IsBlocked("shop.example", Allow("shop.example", Now), Now)); // ends at exactly that moment
    }

    [Fact]
    public void Blocking_a_domain_you_allowed_for_a_while_blocks_it_at_once()
    {
        var config = new AppConfig();
        Assert.True(Rules(config, listed: "shop.example").IsBlocked("shop.example", Now));
        ConfigRules.AllowFor(config, "shop.example", Now.AddHours(2), Now);
        Assert.False(Rules(config, listed: "shop.example").IsBlocked("shop.example", Now));

        ConfigRules.SetRule(config, "shop.example", allow: false, Now);
        Assert.True(Rules(config, listed: "shop.example").IsBlocked("shop.example", Now));
        Assert.Equal(RuleKind.UserBlock, Rules(config).Decide("www.shop.example", Now).Kind);
    }

    [Fact]
    public void A_pause_lets_everything_through_except_the_canaries()
    {
        var matcher = Matcher(blocked: ["ads.example"], userBlocked: ["mine.example"]);
        var rules = new RuleSet(matcher, DomainMatcher.Empty, TemporaryAllowances.None, Now.AddMinutes(10));

        Assert.False(rules.IsBlocked("ads.example", Now));
        Assert.False(rules.IsBlocked("mine.example", Now));
        Assert.Equal(new Decision(false, null, RuleKind.Paused, Now.AddMinutes(10)), rules.Decide("ads.example", Now));
        foreach (var canary in FilterLists.Canaries)
        {
            Assert.True(rules.IsBlocked(canary, Now), canary);
            Assert.Equal(RuleKind.Canary, rules.Decide(canary, Now).Kind);
        }
        Assert.Equal(Now.AddMinutes(10), rules.PausedUntil(Now));

        // Once the pause is over, everything is blocked as before.
        var later = Now.AddMinutes(10);
        Assert.Null(rules.PausedUntil(later));
        Assert.True(rules.IsBlocked("ads.example", later));
        Assert.True(rules.PauseEndedBetween(Now, later));
        Assert.False(rules.PauseEndedBetween(later, later.AddSeconds(10)));
    }

    [Fact]
    public void A_canary_you_allowed_stays_allowed_while_paused()
    {
        var allowed = new RuleSet(Matcher(userAllowed: ["icloud.com"]), DomainMatcher.Empty, TemporaryAllowances.None, Now.AddMinutes(10));
        Assert.False(allowed.IsBlocked("mask.icloud.com", Now));
        Assert.False(allowed.IsBlocked("mask.icloud.com", Now.AddHours(1)));

        var forNow = new RuleSet(Matcher(), DomainMatcher.Empty, Allow(FilterLists.FirefoxDohCanary, Now.AddMinutes(5)), Now.AddMinutes(10));
        Assert.False(forNow.IsBlocked(FilterLists.FirefoxDohCanary, Now));
        Assert.True(forNow.IsBlocked(FilterLists.FirefoxDohCanary, Now.AddMinutes(6)));
    }

    [Fact]
    public void Warns_about_scams_only_when_the_decision_blocks()
    {
        var scam = new DomainMatcher(["fake-shop.example"], []);
        var rules = new RuleSet(Matcher(blocked: ["fake-shop.example"]), scam, Allow("allowed.fake-shop.example", Now.AddMinutes(1)), null);
        Assert.True(rules.IsScam("pay.fake-shop.example", Now));
        Assert.False(rules.IsScam("allowed.fake-shop.example", Now));

        var yours = new RuleSet(Matcher(userBlocked: ["fake-shop.example"]), scam, TemporaryAllowances.None, null);
        Assert.True(yours.IsScam("fake-shop.example", Now)); // blocked by you, and a known scam

        var paused = new RuleSet(Matcher(blocked: ["fake-shop.example"]), scam, TemporaryAllowances.None, Now.AddMinutes(1));
        Assert.False(paused.IsScam("fake-shop.example", Now));
    }

    [Fact]
    public void Tells_which_allowances_ran_out()
    {
        var allowances = new TemporaryAllowances(new Dictionary<string, DateTimeOffset>
        {
            ["a.example"] = Now.AddSeconds(5),
            ["b.example"] = Now.AddSeconds(15),
            ["c.example"] = Now.AddSeconds(-5),
        });
        Assert.Equal(["a.example"], allowances.EndedBetween(Now, Now.AddSeconds(10)));
        Assert.Equal(["b.example"], allowances.EndedBetween(Now.AddSeconds(10), Now.AddSeconds(20)));
        Assert.Empty(allowances.EndedBetween(Now.AddSeconds(20), Now.AddSeconds(30)));
    }

    [Fact]
    public void Setting_a_rule_replaces_the_old_one_and_ends_an_allowance_for_that_domain()
    {
        var config = new AppConfig
        {
            BlockedDomains = ["z.example", "Shop.Example."],
            TemporarilyAllowed = new()
            {
                ["shop.example"] = Now.AddMinutes(10),
                ["cdn.shop.example"] = Now.AddMinutes(10),
                ["example"] = Now.AddMinutes(10), // not a domain
                ["old.example"] = Now.AddMinutes(-1),
            },
        };

        ConfigRules.SetRule(config, "SHOP.example", allow: true, Now);
        Assert.Equal(["z.example"], config.BlockedDomains);
        Assert.Equal(["shop.example"], config.AllowedDomains);
        Assert.Equal(["cdn.shop.example"], config.TemporarilyAllowed.Keys);

        ConfigRules.SetRule(config, "a.example", allow: false, Now);
        ConfigRules.SetRule(config, "shop.example", allow: false, Now);
        Assert.Equal(["a.example", "shop.example", "z.example"], config.BlockedDomains);
        Assert.Empty(config.AllowedDomains);
        Assert.Throws<ArgumentException>(() => ConfigRules.SetRule(config, "not a domain", allow: false, Now));
    }

    [Fact]
    public void Allowing_for_a_while_forgets_allowances_that_ran_out()
    {
        var config = new AppConfig { TemporarilyAllowed = new() { ["old.example"] = Now.AddMinutes(-1) } };
        ConfigRules.AllowFor(config, "Shop.Example", Now.AddMinutes(10), Now);
        Assert.Equal(new Dictionary<string, DateTimeOffset> { ["shop.example"] = Now.AddMinutes(10) }, config.TemporarilyAllowed);

        ConfigRules.AllowFor(config, "shop.example", Now.AddMinutes(30), Now);
        Assert.Equal(Now.AddMinutes(30), config.TemporarilyAllowed["shop.example"]);
    }

    [Fact]
    public void Forgetting_removes_every_rule_of_your_own_for_that_domain()
    {
        var config = new AppConfig
        {
            BlockedDomains = ["shop.example"],
            AllowedDomains = ["SHOP.example"],
            TemporarilyAllowed = new() { ["shop.example"] = Now.AddMinutes(10), ["other.example"] = Now.AddMinutes(10) },
        };
        Assert.Equal(3, ConfigRules.Forget(config, "shop.example.", Now));
        Assert.Empty(config.BlockedDomains);
        Assert.Empty(config.AllowedDomains);
        Assert.Equal(["other.example"], config.TemporarilyAllowed.Keys);
        Assert.Equal(0, ConfigRules.Forget(config, "shop.example", Now));
    }

    private static RuleSet Rules(AppConfig config, params string[] listed) => new(
        Matcher(blocked: listed, userBlocked: [.. config.BlockedDomains], userAllowed: [.. config.AllowedDomains]),
        DomainMatcher.Empty,
        new TemporaryAllowances(config.TemporarilyAllowed),
        config.PausedUntil);
}
