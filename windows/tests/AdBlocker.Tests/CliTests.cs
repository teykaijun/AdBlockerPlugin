using AdBlocker.Cli;
using AdBlocker.Config;
using AdBlocker.Core;
using AdBlocker.Filtering;

namespace AdBlocker.Tests;

public class ArgumentsTests
{
    [Fact]
    public void Reads_arguments_options_and_flags()
    {
        var options = new Arguments(["shop.example", "--for", "10m", "-f"], valued: ["--for"], flags: ["-f", "--follow"]);
        Assert.Equal(["shop.example"], options.Positional);
        Assert.Equal("10m", options.Value("--for"));
        Assert.True(options.Flag("-f", "--follow"));
        Assert.Null(options.Value("--other"));
    }

    [Fact]
    public void Takes_a_value_after_an_equals_sign()
    {
        var options = new Arguments(["--for=10m", "--PORT=5353", "--name="], valued: ["--for", "--port", "--name"], flags: []);
        Assert.Equal("10m", options.Value("--for"));
        Assert.Equal(5353, options.Int("--port", 53, 1, 65_535)); // names don't depend on case
        Assert.Equal("", options.Value("--name")); // empty, which the command then refuses
    }

    [Fact]
    public void Refuses_an_option_without_its_value()
    {
        var error = Assert.Throws<UserError>(() => new Arguments(["shop.example", "--for"], valued: ["--for"], flags: []));
        Assert.Equal("--for needs a value.", error.Message);
    }

    [Fact]
    public void Refuses_options_it_does_not_know()
    {
        var error = Assert.Throws<UserError>(() => new Arguments(["--forever"], valued: ["--for"], flags: []));
        Assert.StartsWith("Unknown option --forever.", error.Message);
    }

    [Fact]
    public void Refuses_a_value_for_a_flag()
    {
        var error = Assert.Throws<UserError>(() => new Arguments(["--verbose=yes"], valued: [], flags: ["--verbose"]));
        Assert.StartsWith("Unknown option --verbose=yes.", error.Message);
    }

    [Fact]
    public void Checks_numbers_and_how_many_arguments_there_are()
    {
        Assert.Equal(["-"], new Arguments(["-"], [], []).Positional); // a lone dash is an argument

        var options = new Arguments(["a", "b", "-n", "0"], valued: ["-n"], flags: []);
        Assert.Throws<UserError>(() => options.Int("-n", 30, 1, 100));
        Assert.Equal(30, new Arguments([], ["-n"], []).Int("-n", 30, 1, 100));
        Assert.Equal("Unexpected argument \"b\".", Assert.Throws<UserError>(() => options.NoMoreThan(1)).Message);
        Assert.Equal("Missing domain.", Assert.Throws<UserError>(() => options.Required(2, "domain")).Message);
    }
}

public class EndTimeTests
{
    private static DateTimeOffset Local(int day, int hour, int minute) => new(new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local));

    [Fact]
    public void Shows_the_day_only_when_it_is_not_today()
    {
        var now = Local(14, 15, 45); // a Saturday
        Assert.Equal("15:55", Durations.EndTime(Local(14, 15, 55), now));
        Assert.Equal("23:59", Durations.EndTime(Local(14, 23, 59), now));
        Assert.Equal("Sun 00:00", Durations.EndTime(Local(15, 0, 0), now));
        Assert.Equal("Sun 15:45", Durations.EndTime(now + Durations.Longest, now));
        Assert.Equal("17:00", Durations.EndTime(Local(14, 17, 0).ToUniversalTime(), now)); // shown in local time
    }
}

/// <summary>"adblocker check" must say what DNS does right now, so it is worked out by the same rules.</summary>
public sealed class CheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 14, 15, 0, 0, DateTimeKind.Local));
    private readonly AppPaths _paths = TestPaths.Temporary();
    private readonly FilterLists _lists;

    public CheckTests()
    {
        _paths.EnsureCreated();
        _lists = new FilterLists(_paths);
        File.WriteAllText(_lists.CachedFile("hagezi-normal"), "||shop.example^\n||ads.example.com^\n@@||example.com^\n");
        File.WriteAllText(_lists.CachedFile("hagezi-fake"), "||fake-shop.example^\n");
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private Commands.CheckAnswer Check(AppConfig config, string domain) => Commands.CheckDomain(_lists, config, domain, Now);

    [Fact]
    public void Names_the_list_that_blocks_a_domain()
    {
        var answer = Check(new AppConfig(), "cdn.shop.example");
        Assert.True(answer.Blocked);
        Assert.Equal("cdn.shop.example is BLOCKED: shop.example (which covers cdn.shop.example) is listed by HaGeZi Multi Normal.", answer.Verdict);
        Assert.Equal(["To allow it: adblocker allow cdn.shop.example", "Just for a while: adblocker allow cdn.shop.example --for 10m"], answer.Notes);

        Assert.Equal("nothing.example is not blocked.", Check(new AppConfig(), "nothing.example").Verdict);
    }

    [Fact]
    public void Says_when_a_list_exception_lets_a_domain_through()
    {
        var answer = Check(new AppConfig(), "ads.example.com");
        Assert.False(answer.Blocked);
        Assert.Equal("ads.example.com is allowed: example.com (which covers ads.example.com) is an exception by HaGeZi Multi Normal.", answer.Verdict);
    }

    [Fact]
    public void Your_own_rules_decide_before_the_lists()
    {
        // Your block of ads.example.com beats the list's exception for example.com.
        var blocked = Check(new AppConfig { BlockedDomains = ["ads.example.com"] }, "ads.example.com");
        Assert.True(blocked.Blocked);
        Assert.Equal("ads.example.com is BLOCKED: you blocked ads.example.com.", blocked.Verdict);

        // Allowing example.com and blocking ads.example.com: the more specific rule decides.
        var config = new AppConfig { AllowedDomains = ["example.com"], BlockedDomains = ["ads.example.com"] };
        Assert.True(Check(config, "ads.example.com").Blocked);
        var allowed = Check(config, "www.example.com");
        Assert.False(allowed.Blocked);
        Assert.Equal("www.example.com is allowed: you allowed example.com (which covers www.example.com).", allowed.Verdict);
    }

    [Fact]
    public void Tells_how_long_an_allowance_lasts_and_what_comes_after()
    {
        var config = new AppConfig { TemporarilyAllowed = new() { ["shop.example"] = Now.AddMinutes(10) } };
        var answer = Check(config, "cdn.shop.example");
        Assert.False(answer.Blocked);
        Assert.Equal("cdn.shop.example is allowed for now: you allowed shop.example (which covers cdn.shop.example) until 15:10.", answer.Verdict);
        Assert.Equal(
            ["After that it is blocked: shop.example (which covers cdn.shop.example) is listed by HaGeZi Multi Normal.",
             "To always allow it: adblocker allow cdn.shop.example"],
            answer.Notes);

        // Your block of a subdomain still beats an allowance for its parent...
        config.BlockedDomains = ["cdn.shop.example"];
        Assert.Equal("cdn.shop.example is BLOCKED: you blocked cdn.shop.example.", Check(config, "cdn.shop.example").Verdict);

        // ...and an allowance for a subdomain beats your block of its parent.
        var other = new AppConfig { BlockedDomains = ["shop.example"], TemporarilyAllowed = new() { ["cdn.shop.example"] = Now.AddMinutes(10) } };
        var forNow = Check(other, "cdn.shop.example");
        Assert.Equal("cdn.shop.example is allowed for now: you allowed cdn.shop.example until 15:10.", forNow.Verdict);
        Assert.Equal("After that it is blocked: you blocked shop.example (which covers cdn.shop.example).", forNow.Notes[0]);

        // An allowance that ran out does nothing.
        var expired = new AppConfig { TemporarilyAllowed = new() { ["shop.example"] = Now.AddMinutes(-1) } };
        Assert.True(Check(expired, "cdn.shop.example").Blocked);
    }

    [Fact]
    public void Blocking_a_domain_allowed_for_a_while_blocks_it_at_once()
    {
        // What "allow shop.example --for 2h" and then "block shop.example" change.
        var config = new AppConfig();
        Commands.ApplyRule(config, "shop.example", allow: true, until: Now.AddHours(2), Now);
        Assert.Equal([("shop.example", Now.AddHours(2))], new TemporaryAllowances(config.TemporarilyAllowed).Active(Now));
        Assert.False(Check(config, "shop.example").Blocked);

        Commands.ApplyRule(config, "shop.example", allow: false, until: null, Now);
        Assert.Empty(config.TemporarilyAllowed);
        Assert.Equal(["shop.example"], config.BlockedDomains);
        Assert.Equal("shop.example is BLOCKED: you blocked shop.example.", Check(config, "shop.example").Verdict);

        // Allowing it for good replaces the block.
        Commands.ApplyRule(config, "Shop.Example.", allow: true, until: null, Now);
        Assert.Empty(config.BlockedDomains);
        Assert.Equal(["shop.example"], config.AllowedDomains);
    }

    [Fact]
    public void Says_a_pause_lets_it_through_for_now()
    {
        var config = new AppConfig { PausedUntil = Now.AddMinutes(10) };
        var answer = Check(config, "cdn.shop.example");
        Assert.False(answer.Blocked);
        Assert.Equal("cdn.shop.example loads for now: blocking is paused until 15:10. After that it is blocked: " +
                     "shop.example (which covers cdn.shop.example) is listed by HaGeZi Multi Normal.", answer.Verdict);

        var notListed = Check(config, "nothing.example");
        Assert.Equal("nothing.example is not blocked.", notListed.Verdict);
        Assert.Equal(["Blocking is paused until 15:10."], notListed.Notes);

        // An allowance that lasts longer than the pause decides after it...
        config.TemporarilyAllowed = new() { ["shop.example"] = Now.AddMinutes(30) };
        var longer = Check(config, "cdn.shop.example");
        Assert.Equal("cdn.shop.example loads for now: blocking is paused until 15:10, " +
                     "and you allowed shop.example (which covers cdn.shop.example) until 15:30.", longer.Verdict);
        Assert.Equal("After that it is blocked: shop.example (which covers cdn.shop.example) is listed by HaGeZi Multi Normal.", longer.Notes[0]);

        // ...one that ends first doesn't.
        config.TemporarilyAllowed = new() { ["shop.example"] = Now.AddMinutes(5) };
        Assert.StartsWith("cdn.shop.example loads for now: blocking is paused until 15:10. After that it is blocked:", Check(config, "cdn.shop.example").Verdict);
    }

    [Fact]
    public void Says_what_AdBlocker_keeps_blocked_even_while_paused()
    {
        var answer = Check(new AppConfig { PausedUntil = Now.AddMinutes(10) }, FilterLists.FirefoxDohCanary);
        Assert.True(answer.Blocked);
        Assert.Equal("use-application-dns.net is BLOCKED: use-application-dns.net is blocked by AdBlocker (keeps Firefox on system DNS).", answer.Verdict);
        Assert.Contains("on purpose", Assert.Single(answer.Notes));
    }

    [Fact]
    public void Warns_about_scam_sites()
    {
        var answer = Check(new AppConfig(), "pay.fake-shop.example");
        Assert.True(answer.Blocked);
        Assert.Equal("pay.fake-shop.example is BLOCKED: fake-shop.example (which covers pay.fake-shop.example) is listed by HaGeZi Fake shops and scams.", answer.Verdict);
        Assert.StartsWith("It's on a list of scams and fake shops", answer.Notes[0]);
        Assert.DoesNotContain(Check(new AppConfig(), "cdn.shop.example").Notes, n => n.Contains("scam", StringComparison.Ordinal));
    }
}

/// <summary>What "stop" and "uninstall" do with network settings the service left pointing at AdBlocker.</summary>
public sealed class LeftoverSettingsTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Temporary();

    public LeftoverSettingsTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    [Fact]
    public void Has_nothing_to_do_when_nothing_was_left_behind() =>
        Assert.Equal(Commands.Leftovers.None, Commands.RestoreWhatTheServiceLeft(_paths));

    [Fact]
    public void Leaves_the_settings_and_the_folder_to_a_console_window_that_still_runs()
    {
        using (BlockerLock.Acquire(_paths))
        {
            // Also before it saved any settings (no network yet, say): "uninstall --purge" keeps the
            // folder for that window, which puts the settings back when it closes.
            Assert.Equal(Commands.Leftovers.InUse, Commands.RestoreWhatTheServiceLeft(_paths));
            File.WriteAllText(_paths.DnsStateFile, """{ "adapters": [] }""");
            Assert.Equal(Commands.Leftovers.InUse, Commands.RestoreWhatTheServiceLeft(_paths));
            Assert.True(File.Exists(_paths.DnsStateFile));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{ not json")]
    public void Tells_unreadable_saved_settings_apart_from_a_console_window(string contents)
    {
        File.WriteAllText(_paths.DnsStateFile, contents);
        // "stop" doesn't say the settings are restored, and "uninstall --purge" doesn't wait for a
        // console window that isn't there.
        Assert.Equal(Commands.Leftovers.Unreadable, Commands.RestoreWhatTheServiceLeft(_paths));
        Assert.True(File.Exists(_paths.DnsStateFile)); // left as it is
    }
}

public class CommandHelperTests
{
    [Fact]
    public void Escapes_control_characters_an_older_log_may_hold()
    {
        const string line = "2026-11-14 15:00:00  BLOCKED  A      x\u001b[2J\u009b31m.ads.example";
        Assert.Equal(@"2026-11-14 15:00:00  BLOCKED  A      x\027[2J\155" + "31m.ads.example", Terminal.PrintableLine(line));
    }

    [Fact]
    public void Leaves_ordinary_and_already_escaped_log_lines_alone()
    {
        const string line = @"2026-11-14 15:00:00  BLOCKED  A      a\010b.ads.example";
        Assert.Same(line, Terminal.PrintableLine(line));
    }

    [Fact]
    public void Gives_lists_you_add_an_id_no_other_list_has()
    {
        var at = new DateTime(2026, 11, 14, 15, 30, 0, DateTimeKind.Utc);
        Assert.Equal("custom-20261114153000", Commands.NewListId([], at));
        Assert.Equal("custom-20261114153000-2", Commands.NewListId(["hagezi-normal", "custom-20261114153000"], at));
        Assert.Equal("custom-20261114153000-3", Commands.NewListId(["Custom-20261114153000", "custom-20261114153000-2"], at));
        Assert.True(FilterLists.IsValidId(Commands.NewListId(["custom-20261114153000"], at)));
    }
}
