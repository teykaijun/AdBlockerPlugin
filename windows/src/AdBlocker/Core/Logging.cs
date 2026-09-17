using System.Text;
using System.Threading.Channels;
using AdBlocker.Dns;

namespace AdBlocker.Core;

public interface ILog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

/// <summary>Log for the Windows service, which has no console.</summary>
public sealed class FileLog(string path) : ILog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _lock = new();

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the blocker down.
            }
        }
    }
}

public readonly record struct QueryEvent(DateTimeOffset Time, string Name, ushort Type, bool Blocked)
{
    public string Format() => $"{Time:yyyy-MM-dd HH:mm:ss}  {(Blocked ? "BLOCKED" : "allowed")}  {DnsMessage.TypeName(Type),-5}  {Name}";
}

/// <summary>
/// Writes lookups to logs\queries.log (rotated at 10 MB) and optionally echoes them,
/// on a background task so answering queries never waits for the disk.
/// </summary>
public sealed class QueryLog : IAsyncDisposable
{
    private const long MaxBytes = 10 * 1024 * 1024;

    private readonly string _path;
    private readonly Action<QueryEvent>? _echo;
    private readonly Channel<(QueryEvent Event, bool ToFile, bool ToEcho)> _pending =
        Channel.CreateBounded<(QueryEvent, bool, bool)>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    private readonly Task _writer;

    public QueryLog(string path, Action<QueryEvent>? echo)
    {
        _path = path;
        _echo = echo;
        _writer = Task.Run(WriteAsync);
    }

    public void Add(QueryEvent query, bool toFile, bool toEcho)
    {
        if (toFile || (toEcho && _echo is not null)) _pending.Writer.TryWrite((query, toFile, toEcho));
    }

    private async Task WriteAsync()
    {
        var lines = new StringBuilder();
        while (await _pending.Reader.WaitToReadAsync())
        {
            lines.Clear();
            while (_pending.Reader.TryRead(out var item))
            {
                if (item.ToEcho) _echo?.Invoke(item.Event);
                if (item.ToFile) lines.AppendLine(item.Event.Format());
            }
            if (lines.Length == 0) continue;
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) File.Move(_path, _path + ".1", overwrite: true);
                await File.AppendAllTextAsync(_path, lines.ToString());
            }
            catch (IOException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        await _writer;
    }
}
