using System.Globalization;
using AdBlocker.Core;

namespace AdBlocker.Cli;

/// <summary>Durations typed on the command line: "10" or "10m" for minutes, "2h" for hours.</summary>
internal static class Durations
{
    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    /// <param name="tooLong">What to say about more than a day.</param>
    public static TimeSpan Parse(string text, string tooLong = "That's longer than a day. Use a permanent rule instead.")
    {
        var trimmed = text.Trim().ToLowerInvariant();
        var hours = trimmed.EndsWith('h');
        var number = hours || trimmed.EndsWith('m') ? trimmed[..^1] : trimmed;
        if (!int.TryParse(number, out var value) || value < 1)
        {
            throw new UserError($"\"{text}\" is not a duration. Use minutes like 10 or 10m, or hours like 2h.");
        }
        // Checked on the number: a huge number of hours doesn't even fit in a TimeSpan.
        if (value > (hours ? Longest.TotalHours : Longest.TotalMinutes))
        {
            throw new UserError(tooLong);
        }
        return hours ? TimeSpan.FromHours(value) : TimeSpan.FromMinutes(value);
    }

    public static string Describe(TimeSpan duration) => duration.TotalMinutes switch
    {
        < 60 => Plural((int)Math.Round(duration.TotalMinutes), "minute"),
        _ when duration.Minutes == 0 => Plural((int)duration.TotalHours, "hour"),
        _ => $"{Plural((int)duration.TotalHours, "hour")} {Plural(duration.Minutes, "minute")}",
    };

    /// <summary>
    /// When a pause or an allowance ends, for messages: "15:45" today, and with the day, such as
    /// "Sat 09:00", when that is another day. Both last up to a day, so the time alone can mislead.
    /// </summary>
    public static string EndTime(DateTimeOffset end, DateTimeOffset now)
    {
        var local = end.LocalDateTime;
        var format = local.Date == now.LocalDateTime.Date ? "HH:mm" : "ddd HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
