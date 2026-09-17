using AdBlocker.Core;

namespace AdBlocker.Cli;

/// <summary>Console output with a little colour.</summary>
internal static class Terminal
{
    private static readonly Lock WriteLock = new();

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Heading(string text) => Write(ConsoleColor.Cyan, text);

    public static void Ok(string text) => Write(ConsoleColor.Green, text);

    public static void Warn(string text) => Write(ConsoleColor.Yellow, text);

    public static void Dim(string text) => Write(ConsoleColor.DarkGray, text);

    public static void Error(string text)
    {
        lock (WriteLock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(text);
            Console.ResetColor();
        }
    }

    /// <summary>One line per lookup while <c>adblocker run</c> is active.</summary>
    public static void Query(QueryEvent query) =>
        Write(query.Blocked ? ConsoleColor.Red : ConsoleColor.DarkGray,
            $"{query.Time:HH:mm:ss}  {(query.Blocked ? "blocked" : "allowed")}  {query.Name}");

    /// <summary>Two-column "label  value" lines.</summary>
    public static void Pairs(params (string Label, string Value)[] pairs)
    {
        var width = pairs.Max(p => p.Label.Length) + 2;
        foreach (var (label, value) in pairs) Line($"  {label.PadRight(width)}{value}");
    }

    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        Dim("  " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in rows) Line("  " + string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    private static void Write(ConsoleColor color, string text)
    {
        lock (WriteLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}

internal sealed class ConsoleLog : ILog
{
    public void Info(string message) => Terminal.Line(message);

    public void Warn(string message) => Terminal.Warn(message);

    public void Error(string message) => Terminal.Error(message);
}

/// <summary>Positional arguments plus <c>--name value</c> / <c>--name=value</c> options.</summary>
internal sealed class Arguments
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="valued">Options that take a value, like <c>--port</c>.</param>
    /// <param name="flags">Options that are simply present or not.</param>
    public Arguments(IEnumerable<string> args, string[] valued, string[] flags)
    {
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (!arg.StartsWith('-') || arg == "-")
            {
                _positional.Add(arg);
                continue;
            }
            var eq = arg.IndexOf('=');
            var name = eq > 0 ? arg[..eq] : arg;
            if (valued.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (eq > 0) _options[name] = arg[(eq + 1)..];
                else if (e.MoveNext()) _options[name] = e.Current;
                else throw new UserError($"{name} needs a value.");
            }
            else if (flags.Contains(name, StringComparer.OrdinalIgnoreCase) && eq < 0)
            {
                _options[name] = null;
            }
            else
            {
                throw new UserError($"Unknown option {arg}. Run \"adblocker help\" for the list of commands.");
            }
        }
    }

    public IReadOnlyList<string> Positional => _positional;

    public bool Flag(params string[] names) => names.Any(_options.ContainsKey);

    public int Int(string name, int fallback, int min, int max)
    {
        if (!_options.TryGetValue(name, out var text)) return fallback;
        return int.TryParse(text, out var value) && value >= min && value <= max
            ? value
            : throw new UserError($"{name} must be a number from {min} to {max}.");
    }

    public string Required(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new UserError($"Missing {what}.");

    public void NoMoreThan(int count)
    {
        if (_positional.Count > count) throw new UserError($"Unexpected argument \"{_positional[count]}\".");
    }
}
