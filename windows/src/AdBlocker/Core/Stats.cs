using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdBlocker.Config;

namespace AdBlocker.Core;

/// <summary>The contents of stats.json.</summary>
public sealed class StatsSnapshot
{
    public DateTimeOffset Since { get; set; } = DateTimeOffset.Now;

    /// <summary>When a running blocker last saved; used to tell whether one is active.</summary>
    public DateTimeOffset Updated { get; set; }

    public bool Running { get; set; }

    public long Queries { get; set; }

    public long Blocked { get; set; }

    /// <summary>The day the "today" counters belong to (yyyy-MM-dd, local time).</summary>
    public string Day { get; set; } = "";

    public long QueriesToday { get; set; }

    public long BlockedToday { get; set; }

    /// <summary>Whether a blocker saved recently enough to be considered running.</summary>
    [JsonIgnore]
    public bool IsLive => Running && DateTimeOffset.Now - Updated < TimeSpan.FromSeconds(45);

    public StatsSnapshot Copy() => (StatsSnapshot)MemberwiseClone();
}

/// <summary>Lookup counters, saved to stats.json every few seconds.</summary>
public sealed class Stats(string file)
{
    private readonly Lock _lock = new();
    private StatsSnapshot _current = Read(file) ?? new StatsSnapshot();

    public static string ResetMarker(AppPaths paths) => paths.StatsFile + ".reset";

    public void Record(bool blocked)
    {
        lock (_lock)
        {
            RollOver();
            _current.Queries++;
            _current.QueriesToday++;
            if (blocked)
            {
                _current.Blocked++;
                _current.BlockedToday++;
            }
        }
    }

    public StatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            RollOver();
            return _current.Copy();
        }
    }

    public void Reset()
    {
        lock (_lock) _current = new StatsSnapshot { Running = _current.Running };
    }

    public void Save(bool running)
    {
        StatsSnapshot snapshot;
        lock (_lock)
        {
            RollOver();
            _current.Running = running;
            _current.Updated = DateTimeOffset.Now;
            snapshot = _current.Copy();
        }
        try
        {
            AtomicFile.WriteAllText(file, JsonSerializer.Serialize(snapshot, AppJson.Default.StatsSnapshot));
        }
        catch (IOException)
        {
        }
    }

    public static StatsSnapshot? Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), AppJson.Default.StatsSnapshot) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private void RollOver()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_current.Day == today) return;
        _current.Day = today;
        _current.QueriesToday = 0;
        _current.BlockedToday = 0;
    }
}
