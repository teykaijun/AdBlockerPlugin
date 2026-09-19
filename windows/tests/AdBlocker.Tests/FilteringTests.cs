using AdBlocker.Config;
using AdBlocker.Filtering;

namespace AdBlocker.Tests;

public class DomainMatcherTests
{
    private readonly DomainMatcher _matcher = new(
        ["doubleclick.net", "ads.example.com", "tracker.io"],
        ["safe.ads.example.com", "tracker.io"]);

    [Theory]
    [InlineData("doubleclick.net")]
    [InlineData("googleads.g.doubleclick.net")]
    [InlineData("ads.example.com")]
    [InlineData("cdn.ads.example.com")]
    public void Blocks_listed_domains_and_subdomains(string name) => Assert.True(_matcher.IsBlocked(name));

    [Theory]
    [InlineData("example.com")]
    [InlineData("www.example.com")]
    [InlineData("notdoubleclick.net")]
    [InlineData("doubleclick.net.example.org")]
    [InlineData("net")]
    [InlineData("")]
    public void Leaves_parents_and_lookalikes_alone(string name) => Assert.False(_matcher.IsBlocked(name));

    [Theory]
    [InlineData("safe.ads.example.com")]
    [InlineData("img.safe.ads.example.com")]
    [InlineData("tracker.io")]
    [InlineData("pixel.tracker.io")]
    public void Lets_exceptions_win(string name) => Assert.False(_matcher.IsBlocked(name));

    [Fact]
    public void Explains_decisions()
    {
        Assert.Equal(new MatchResult(true, "doubleclick.net"), _matcher.Match("Stats.DoubleClick.NET."));
        Assert.Equal(new MatchResult(false, "safe.ads.example.com"), _matcher.Match("x.safe.ads.example.com"));
        Assert.Equal(new MatchResult(false, null), _matcher.Match("example.org"));
        Assert.Equal(3, _matcher.BlockedCount);
    }
}

public class FilterListsTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Temporary();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    [Fact]
    public void Embeds_every_shared_list()
    {
        var files = Directory.GetFiles(Path.Combine(TestPaths.RepositoryRoot, "filters"), "*.txt").Select(Path.GetFileNameWithoutExtension);
        Assert.Equal(files.Order(), FilterLists.BuiltIn.Select(l => l.Id).Order());
        Assert.All(FilterLists.BuiltIn, l => Assert.True(FilterLists.CountDomains(l.Id) > 0, l.Id));
        Assert.Contains(FilterLists.BuiltIn, l => l.Id == "ads" && l.EnabledByDefault);
        Assert.Contains(FilterLists.BuiltIn, l => l.Id == "social" && !l.EnabledByDefault);
    }

    [Fact]
    public void Uses_list_defaults_until_the_user_changes_them()
    {
        var config = new AppConfig();
        var lists = new FilterLists(_paths);

        var matcher = lists.BuildMatcher(config);
        Assert.True(matcher.IsBlocked("pagead2.googlesyndication.com"));
        Assert.True(matcher.IsBlocked("www.google-analytics.com"));
        Assert.False(matcher.IsBlocked("connect.facebook.net"));
        Assert.True(matcher.IsBlocked(FilterLists.FirefoxDohCanary));

        config.Lists["ads"] = false;
        config.Lists["social"] = true;
        matcher = lists.BuildMatcher(config);
        Assert.False(matcher.IsBlocked("pagead2.googlesyndication.com"));
        Assert.True(matcher.IsBlocked("connect.facebook.net"));
    }

    [Fact]
    public void Applies_the_users_own_rules_over_the_lists()
    {
        var config = new AppConfig
        {
            BlockedDomains = ["Example.org", "not a domain"],
            AllowedDomains = ["googlesyndication.com", FilterLists.FirefoxDohCanary],
        };
        var matcher = new FilterLists(_paths).BuildMatcher(config);
        Assert.True(matcher.IsBlocked("www.example.org"));
        Assert.False(matcher.IsBlocked("pagead2.googlesyndication.com"));
        Assert.False(matcher.IsBlocked(FilterLists.FirefoxDohCanary));
    }

    [Fact]
    public void Loads_downloaded_community_lists_that_are_enabled()
    {
        var lists = new FilterLists(_paths);
        Directory.CreateDirectory(_paths.ListsDirectory);
        File.WriteAllText(lists.CachedFile("hagezi-normal"), "||from-community.example^\n");
        File.WriteAllText(lists.CachedFile("stevenblack"), "0.0.0.0 from-hosts.example\n");

        var matcher = lists.BuildMatcher(new AppConfig());
        Assert.True(matcher.IsBlocked("from-community.example"));
        Assert.False(matcher.IsBlocked("from-hosts.example")); // that preset is off by default

        var sources = lists.FindSources(new AppConfig { BlockedDomains = ["from-community.example"] }, "from-community.example", allow: false);
        Assert.Equal(new[] { "your blocked domains", "HaGeZi Multi Normal" }, sources);
    }

    [Fact]
    public void Tells_scam_sites_apart_from_ordinary_blocked_domains()
    {
        var lists = new FilterLists(_paths);
        Directory.CreateDirectory(_paths.ListsDirectory);
        File.WriteAllText(lists.CachedFile("hagezi-normal"), "||ads.example^\n");
        File.WriteAllText(lists.CachedFile("hagezi-fake"), "||fake-shop.example^\n@@||real-shop.example^\n");

        var scam = lists.BuildScamMatcher(new AppConfig());
        Assert.True(scam.IsBlocked("fake-shop.example"));
        Assert.True(scam.IsBlocked("pay.fake-shop.example"));
        Assert.False(scam.IsBlocked("ads.example")); // blocked, but only an ad server
        Assert.False(scam.IsBlocked("real-shop.example"));

        // A domain the user allowed is not blocked at all, so it is never reported as a scam.
        var allowing = new AppConfig { AllowedDomains = ["fake-shop.example"] };
        Assert.False(lists.BuildScamMatcher(allowing).IsBlocked("fake-shop.example"));
        Assert.False(lists.BuildMatcher(allowing).IsBlocked("fake-shop.example"));
    }

    [Fact]
    public void Merges_presets_with_the_users_community_list_settings()
    {
        var config = new AppConfig
        {
            CommunityLists =
            [
                new CommunityListSetting { Id = "hagezi-normal", Enabled = false },
                new CommunityListSetting { Id = "adguard-dns", Enabled = true },
                new CommunityListSetting { Id = "custom-1", Title = "Mine", Url = "https://example.com/hosts", Enabled = true },
                new CommunityListSetting { Id = "..\\evil", Title = "Bad", Url = "https://example.com/x", Enabled = true },
            ],
        };
        var lists = FilterLists.CommunityLists(config);

        Assert.False(lists.Single(l => l.Id == "hagezi-normal").Enabled);
        Assert.True(lists.Single(l => l.Id == "adguard-dns").Enabled);
        Assert.False(lists.Single(l => l.Id == "stevenblack").Enabled);
        Assert.True(lists.Single(l => l.Id == "hagezi-popupads").Enabled); // on by default
        Assert.True(lists.Single(l => l.Id == "hagezi-fake").Enabled); // on by default
        var custom = lists.Single(l => l.Custom);
        Assert.Equal(("custom-1", "Mine", true), (custom.Id, custom.Title, custom.Enabled));
        Assert.Throws<ArgumentException>(() => new FilterLists(_paths).CachedFile("..\\evil"));
    }

    [Fact]
    public void Saves_and_loads_the_configuration()
    {
        var saved = ConfigStore.Update(_paths, c =>
        {
            c.BlockedDomains.Add("ads.example.com");
            c.Upstream = ["quad9"];
            c.Lists["social"] = true;
        });
        var loaded = ConfigStore.Load(_paths);
        Assert.Equal(saved.BlockedDomains, loaded.BlockedDomains);
        Assert.Equal(new[] { "quad9" }, loaded.Upstream);
        Assert.True(loaded.Lists["social"]);
        Assert.Contains("Tailscale", loaded.ExcludedAdapters); // VPN adapters are left alone by default
        Assert.Contains("\"blockedDomains\"", File.ReadAllText(_paths.ConfigFile));

        File.WriteAllText(_paths.ConfigFile, "{ not json");
        Assert.Throws<AdBlocker.Core.UserError>(() => ConfigStore.Load(_paths));
    }
}
