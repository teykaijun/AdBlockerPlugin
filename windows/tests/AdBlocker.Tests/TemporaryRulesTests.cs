using AdBlocker.Cli;
using AdBlocker.Core;
using AdBlocker.Filtering;

namespace AdBlocker.Tests;

public class TemporaryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allows_a_domain_and_its_subdomains_until_the_time_runs_out()
    {
        var allowances = new TemporaryAllowances(new Dictionary<string, DateTimeOffset>
        {
            ["Shop.Example.com."] = Now.AddMinutes(10),
            ["old.example.org"] = Now.AddMinutes(-1),
            ["not a domain"] = Now.AddMinutes(10),
        });

        Assert.True(allowances.Allows("shop.example.com", Now));
        Assert.True(allowances.Allows("cdn.shop.example.com.", Now));
        Assert.False(allowances.Allows("example.com", Now));
        Assert.False(allowances.Allows("notshop.example.com", Now));
        Assert.False(allowances.Allows("old.example.org", Now));
        Assert.False(allowances.Allows("shop.example.com", Now.AddMinutes(10)));

        Assert.Equal([("shop.example.com", Now.AddMinutes(10))], allowances.Active(Now));
        Assert.False(TemporaryAllowances.None.Allows("shop.example.com", Now));
    }

    [Fact]
    public void Forgets_entries_that_ran_out()
    {
        var pruned = TemporaryAllowances.Prune(
            new Dictionary<string, DateTimeOffset> { ["a.example"] = Now.AddMinutes(1), ["b.example"] = Now.AddMinutes(-1) },
            Now);
        Assert.Equal(["a.example"], pruned.Keys);
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("10m", 10)]
    [InlineData(" 45M ", 45)]
    [InlineData("2h", 120)]
    [InlineData("24h", 1440)]
    [InlineData("1440m", 1440)]
    public void Reads_durations(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), Durations.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten")]
    [InlineData("10min")]
    [InlineData("25h")]
    [InlineData("1441")]
    [InlineData("999999999h")] // more hours than a TimeSpan can hold
    [InlineData("2147483647m")]
    [InlineData("99999999999")] // not even an int
    public void Refuses_durations_it_cannot_use(string text) =>
        Assert.Throws<UserError>(() => Durations.Parse(text));

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(10, "10 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "1 hour 30 minutes")]
    [InlineData(120, "2 hours")]
    public void Describes_durations(int minutes, string text) =>
        Assert.Equal(text, Durations.Describe(TimeSpan.FromMinutes(minutes)));
}
