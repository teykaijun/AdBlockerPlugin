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
        Assert.Equal(new Decision(true, "doubleclick.net", RuleKind.ListBlock), _matcher.Match("Stats.DoubleClick.NET."));
        Assert.Equal(new Decision(false, "safe.ads.example.com", RuleKind.ListAllow), _matcher.Match("x.safe.ads.example.com"));
        Assert.Equal(new Decision(false, null, RuleKind.None), _matcher.Match("example.org"));
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
    public void Reads_the_presets_shared_with_the_Android_app()
    {
        Assert.Equal(
            ["hagezi-normal", "hagezi-popupads", "hagezi-fake", "hagezi-tif-mini", "hagezi-light", "adguard-dns", "stevenblack"],
            FilterLists.Presets.Select(l => l.Id));
        Assert.Equal(["hagezi-normal", "hagezi-popupads", "hagezi-fake"], FilterLists.Presets.Where(l => l.Enabled).Select(l => l.Id));
        Assert.Equal(["hagezi-fake", "hagezi-tif-mini"], FilterLists.Presets.Where(l => l.Scam).Select(l => l.Id));
        Assert.All(FilterLists.Presets, l =>
        {
            Assert.True(FilterLists.IsValidId(l.Id), l.Id);
            Assert.StartsWith("https://", l.Url);
            Assert.False(string.IsNullOrWhiteSpace(l.Title) || string.IsNullOrWhiteSpace(l.Description), l.Id);
            Assert.False(l.Custom);
        });
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
        Assert.All(FilterLists.PrivateRelayCanaries, name => Assert.True(matcher.IsBlocked(name), name));
        Assert.Equal(new Decision(true, "mask.icloud.com", RuleKind.Canary), matcher.Match("mask.icloud.com"));

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
        // The user can still opt out of the checks AdBlocker blocks on its own.
        Assert.False(matcher.IsBlocked(FilterLists.FirefoxDohCanary));
        Assert.True(matcher.IsBlocked("mask.icloud.com"));
    }

    [Fact]
    public void Keeps_the_canaries_blocked_despite_list_exceptions()
    {
        var lists = new FilterLists(_paths);
        Directory.CreateDirectory(_paths.ListsDirectory);
        File.WriteAllText(lists.CachedFile("hagezi-normal"), "@@||icloud.com^\n@@||use-application-dns.net^\n");

        var matcher = lists.BuildMatcher(new AppConfig());
        Assert.True(matcher.IsBlocked("mask-h2.icloud.com"));
        Assert.True(matcher.IsBlocked(FilterLists.FirefoxDohCanary));
        Assert.False(matcher.IsBlocked("www.icloud.com"));
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
    public void Lets_a_newer_download_replace_a_list_that_is_still_being_parsed()
    {
        var lists = new FilterLists(_paths);
        Directory.CreateDirectory(_paths.ListsDirectory);
        var file = lists.CachedFile("hagezi-normal");
        File.WriteAllText(file, "||old.example^\n");

        using var reader = FilterLists.ReadIntoMemory(file);
        // Windows can't replace a file that is open, however it is shared, so it must be closed by now.
        File.WriteAllText(file + ".download", "||new.example^\n");
        File.Move(file + ".download", file, overwrite: true);

        Assert.Equal("||old.example^", reader.ReadLine()); // the parse goes on with what it read
        Assert.Equal("||new.example^\n", File.ReadAllText(file));
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
        Assert.False(lists.BuildMatcher(allowing).IsBlocked("fake-shop.example"));
        Assert.False(lists.Explain(allowing, "fake-shop.example", DateTimeOffset.Now).Scam);
        Assert.True(lists.Explain(new AppConfig(), "pay.fake-shop.example", DateTimeOffset.Now).Scam);
        Assert.False(lists.Explain(new AppConfig(), "ads.example", DateTimeOffset.Now).Scam);
    }

    [Fact]
    public void Explains_what_decides_and_where_the_rule_comes_from()
    {
        var lists = new FilterLists(_paths);
        Directory.CreateDirectory(_paths.ListsDirectory);
        File.WriteAllText(lists.CachedFile("hagezi-normal"), "||tracker.example^\n@@||ok.tracker.example^\n");
        var now = DateTimeOffset.Now;
        var config = new AppConfig
        {
            BlockedDomains = ["mine.example"],
            TemporarilyAllowed = new() { ["shop.tracker.example"] = now.AddMinutes(10) },
        };

        var listed = lists.Explain(config, "Pixel.Tracker.Example.", now);
        Assert.Equal("pixel.tracker.example", listed.Domain);
        Assert.Equal(new Decision(true, "tracker.example", RuleKind.ListBlock), listed.Decision);
        Assert.Equal(listed.Decision, listed.Lasting);
        Assert.Equal(["HaGeZi Multi Normal"], listed.Sources);

        var exception = lists.Explain(config, "ok.tracker.example", now);
        Assert.Equal(new Decision(false, "ok.tracker.example", RuleKind.ListAllow), exception.Decision);
        Assert.Equal(["HaGeZi Multi Normal"], exception.Sources);

        var own = lists.Explain(config, "www.mine.example", now);
        Assert.Equal(new Decision(true, "mine.example", RuleKind.UserBlock), own.Decision);
        Assert.Equal(["your blocked domains"], own.Sources);

        // Allowed for a while: what happens now, and what happens once the time is up.
        var forNow = lists.Explain(config, "cdn.shop.tracker.example", now);
        Assert.Equal(new Decision(false, "shop.tracker.example", RuleKind.TemporaryAllow, now.AddMinutes(10)), forNow.Decision);
        Assert.Equal(new Decision(true, "tracker.example", RuleKind.ListBlock), forNow.Lasting);
        Assert.Equal(["HaGeZi Multi Normal"], forNow.Sources);

        var canary = lists.Explain(config, "mask.icloud.com", now);
        Assert.Equal(RuleKind.Canary, canary.Decision.Kind);
        Assert.Equal(["AdBlocker (iCloud Private Relay check)"], canary.Sources);
        Assert.Equal(["AdBlocker (keeps Firefox on system DNS)"], lists.Explain(config, FilterLists.FirefoxDohCanary, now).Sources);

        Assert.Equal(new Decision(false, null, RuleKind.None), lists.Explain(config, "example.org", now).Decision);
        Assert.Empty(lists.Explain(config, "example.org", now).Sources);

        // Paused: nothing is blocked for now, but it still says what blocks it afterwards.
        config.PausedUntil = now.AddMinutes(5);
        var paused = lists.Explain(config, "pixel.tracker.example", now);
        Assert.Equal(new Decision(false, null, RuleKind.Paused, now.AddMinutes(5)), paused.Decision);
        Assert.Equal(new Decision(true, "tracker.example", RuleKind.ListBlock), paused.Lasting);
        Assert.Equal(["HaGeZi Multi Normal"], paused.Sources);
        Assert.True(lists.Explain(config, "mask.icloud.com", now).Decision.Blocked);
    }

    [Fact]
    public void Rebuilds_the_matchers_only_when_what_they_are_made_of_changes()
    {
        var config = new AppConfig { BlockedDomains = ["b.example", "a.example"] };
        var before = FilterLists.MatcherInputs(config);

        config.PausedUntil = DateTimeOffset.Now.AddMinutes(10);
        config.TemporarilyAllowed["x.example"] = DateTimeOffset.Now.AddMinutes(10);
        config.Upstream = ["quad9"];
        config.BlockedDomains = ["A.example.", "b.example"];
        Assert.Equal(before, FilterLists.MatcherInputs(config));

        Assert.NotEqual(before, FilterLists.MatcherInputs(new AppConfig { BlockedDomains = ["a.example"] }));
        Assert.NotEqual(before, FilterLists.MatcherInputs(new AppConfig { BlockedDomains = ["a.example", "b.example"], AllowedDomains = ["c.example"] }));
        var social = new AppConfig { BlockedDomains = ["a.example", "b.example"] };
        social.Lists["social"] = true;
        Assert.NotEqual(before, FilterLists.MatcherInputs(social));
        var community = new AppConfig { BlockedDomains = ["a.example", "b.example"] };
        community.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-normal", Enabled = false });
        Assert.NotEqual(before, FilterLists.MatcherInputs(community));
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
    public void Copes_with_the_same_list_twice_and_lists_without_an_id()
    {
        var config = new AppConfig
        {
            CommunityLists =
            [
                new CommunityListSetting { Id = "hagezi-normal", Enabled = true },
                new CommunityListSetting { Id = "HaGeZi-Normal", Enabled = false },
                new CommunityListSetting { Id = "custom-1", Title = "Old", Url = "https://example.com/a", Enabled = true },
                new CommunityListSetting { Id = "custom-1", Title = "New", Url = "https://example.com/b", Enabled = true },
                new CommunityListSetting { Id = "", Url = "https://example.com/c", Enabled = true },
                null!,
            ],
        };
        var lists = FilterLists.CommunityLists(config);
        Assert.False(lists.Single(l => l.Id == "hagezi-normal").Enabled); // the last entry wins
        Assert.Equal("New", lists.Single(l => l.Custom).Title);
        Assert.NotNull(new FilterLists(_paths).BuildMatcher(config));
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

    [Fact]
    public void Repairs_hand_edits_that_are_valid_JSON()
    {
        _paths.EnsureCreated();
        File.WriteAllText(_paths.ConfigFile, """
            {
              "upstream": null,
              "blockedDomains": [null, " ", "ads.example"],
              "allowedDomains": null,
              "temporarilyAllowed": null,
              "lists": null,
              "excludedAdapters": [null, "Tailscale"],
              "communityLists": [
                { "id": null, "enabled": true },
                null,
                { "id": "hagezi-normal", "enabled": true },
                { "id": "Hagezi-Normal", "enabled": false }
              ]
            }
            """);

        var config = ConfigStore.Load(_paths);
        Assert.Equal(["auto"], config.Upstream);
        Assert.Equal(["ads.example"], config.BlockedDomains);
        Assert.Empty(config.AllowedDomains);
        Assert.Empty(config.TemporarilyAllowed);
        Assert.Empty(config.Lists);
        Assert.Equal(["Tailscale"], config.ExcludedAdapters);
        Assert.Equal(("Hagezi-Normal", false), (config.CommunityLists.Single().Id, config.CommunityLists.Single().Enabled));

        // Everything that uses the configuration copes with it.
        var lists = new FilterLists(_paths);
        Assert.True(lists.BuildMatcher(config).IsBlocked("ads.example"));
        Assert.NotNull(lists.BuildScamMatcher(config));
        Assert.NotNull(new TemporaryAllowances(config.TemporarilyAllowed));
        Assert.NotEmpty(FilterLists.MatcherInputs(config));
    }

    [Fact]
    public void Reads_the_configuration_while_someone_else_has_it_open()
    {
        ConfigStore.Update(_paths, c => c.BlockedDomains.Add("ads.example"));
        using (new FileStream(_paths.ConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal(["ads.example"], ConfigStore.Load(_paths).BlockedDomains);
        }
    }
}
