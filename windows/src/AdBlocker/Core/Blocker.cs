using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using AdBlocker.Config;
using AdBlocker.Dns;
using AdBlocker.Filtering;
using AdBlocker.Platform;

namespace AdBlocker.Core;

public sealed class BlockerOptions
{
    public int Port { get; init; } = 53;

    /// <summary>Point the network adapters at AdBlocker (and restore them when stopping).</summary>
    public bool ManageSystemDns { get; init; } = true;

    /// <summary>Echo allowed lookups as well as blocked ones.</summary>
    public bool Verbose { get; init; }

    /// <summary>Receives lookups to show (console mode).</summary>
    public Action<QueryEvent>? Echo { get; init; }
}

/// <summary>
/// The whole blocker: loads the lists, serves DNS on 127.0.0.1 and ::1, points Windows
/// at itself, follows config, list and network changes, and restores the network
/// settings when it stops, whatever the reason.
/// </summary>
public sealed class Blocker(AppPaths paths, BlockerOptions options, ILog log)
{
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ListRefreshInterval = TimeSpan.FromHours(6);

    private readonly FilterLists _lists = new(paths);
    private readonly SystemDns? _systemDns = options.ManageSystemDns ? new SystemDns(paths) : null;
    private readonly HttpClient _dohClient = DohUpstream.CreateClient();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Stats _stats = new(paths.StatsFile);

    private volatile DomainMatcher _matcher = DomainMatcher.Empty;
    private volatile UpstreamPool _upstreams = new([], UpstreamTimeout);
    private volatile AppConfig _config = new();
    private QueryLog? _queryLog;
    private int _networkChanged;
    private bool _servesIPv6;
    private DateTime _lastUpstreamWarning;

    /// <summary>The blocked domains currently loaded (for tests and status).</summary>
    public int BlockedDomainCount => _matcher.BlockedCount;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.ConfigFile)) ConfigStore.Save(paths, new AppConfig()); // so people can see and edit the defaults

        await using var queryLog = _queryLog = new QueryLog(paths.QueryLog, options.Echo);
        Reload(initial: true);

        IPEndPoint[] endpoints = [new(IPAddress.Loopback, options.Port), new(IPAddress.IPv6Loopback, options.Port)];
        await using var server = new DnsServer(endpoints, HandleAsync);
        try
        {
            server.Start();
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new UserError(
                $"Port {options.Port} is already in use, so AdBlocker cannot receive DNS lookups. " +
                "Another DNS program, or Windows' Mobile hotspot / Internet Connection Sharing, may be using it.", e);
        }
        foreach (var (endpoint, error) in server.Failed) log.Warn($"Not listening on {endpoint}: {error.Message}");
        _servesIPv6 = server.BoundEndpoints.Any(e => e.AddressFamily == AddressFamily.InterNetworkV6);
        log.Info($"Listening for DNS lookups on {string.Join(" and ", server.BoundEndpoints)}.");

        using var watcher = WatchFiles();
        NetworkAddressChangedEventHandler onNetworkChange = (_, _) => Signal(network: true);
        NetworkChange.NetworkAddressChanged += onNetworkChange;
        try
        {
            if (_systemDns is not null) ApplySystemDns(initial: true);
            _stats.Save(running: true);

            using var loops = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var first = await Task.WhenAny(
                ProcessChangesAsync(loops.Token),
                RefreshListsAsync(loops.Token),
                SaveStatsAsync(loops.Token));
            await loops.CancelAsync();
            await first; // surfaces the error if a loop failed
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onNetworkChange;
            if (_systemDns is not null) RestoreSystemDns();
            _stats.Save(running: false);
        }
    }

    private async Task<byte[]?> HandleAsync(byte[] query, CancellationToken cancellationToken)
    {
        if (!DnsMessage.TryParseQuery(query, out var question)) return null;

        var blocked = _matcher.IsBlocked(question.Name);
        _stats.Record(blocked);
        var config = _config;
        _queryLog?.Add(
            new QueryEvent(DateTimeOffset.Now, question.Name, question.Type, blocked),
            toFile: blocked || config.LogAllowedQueries,
            toEcho: blocked || options.Verbose);
        if (blocked) return DnsMessage.BlockedResponse(query, question);

        var answer = await _upstreams.QueryAsync(query, cancellationToken);
        if (answer is null)
        {
            WarnUpstreamDown();
            return DnsMessage.FailureResponse(query, question);
        }
        DnsMessage.WriteId(answer, question.Id);
        return answer;
    }

    private void Reload(bool initial)
    {
        var config = ConfigStore.Load(paths);
        foreach (var entry in config.Upstream)
        {
            if (UpstreamSettings.Validate(entry) is { } problem) log.Warn($"Ignoring DNS server {problem}");
        }

        var matcher = _lists.BuildMatcher(config);
        var automatic = _systemDns?.OriginalServers(config.ExcludedAdapters) ?? SystemDns.CurrentServers(config.ExcludedAdapters);
        var servers = UpstreamSettings.Create(config.Upstream, automatic, _dohClient);

        _config = config;
        _matcher = matcher;
        _upstreams = new UpstreamPool(servers, UpstreamTimeout);
        log.Info($"{(initial ? "Loaded" : "Reloaded")} {matcher.BlockedCount:N0} blocked domains. " +
                 $"Allowed lookups go to {string.Join(", ", servers.Select(s => s.Name))}.");
    }

    private void Signal(bool network = false)
    {
        if (network) Interlocked.Exchange(ref _networkChanged, 1);
        _changes.Writer.TryWrite(true);
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        while (await _changes.Reader.WaitToReadAsync(cancellationToken))
        {
            // Let a burst of file or network events settle first.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            while (_changes.Reader.TryRead(out _))
            {
            }

            try
            {
                var resetMarker = Stats.ResetMarker(paths);
                if (File.Exists(resetMarker))
                {
                    _stats.Reset();
                    File.Delete(resetMarker);
                }
                if (Interlocked.Exchange(ref _networkChanged, 0) == 1 && _systemDns is not null) ApplySystemDns(initial: false);
                Reload(initial: false);
            }
            catch (Exception e) when (e is UserError or IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not apply the change: {e.Message}");
            }
        }
    }

    private async Task RefreshListsAsync(CancellationToken cancellationToken)
    {
        using var http = FilterLists.CreateDownloadClient();
        // Give the network a moment; lookups for the download already go through AdBlocker.
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        while (true)
        {
            try
            {
                // A changed list file triggers a reload through the file watcher.
                await _lists.RefreshStaleAsync(_config, http, log.Info, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Warn($"Updating community lists failed: {e.Message}");
            }
            await Task.Delay(ListRefreshInterval, cancellationToken);
        }
    }

    private async Task SaveStatsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken)) _stats.Save(running: true);
    }

    private FileSystemWatcher WatchFiles()
    {
        var watcher = new FileSystemWatcher(paths.Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Created += (_, e) => OnFileEvent(e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnFileEvent(string path)
    {
        var relevant = PathEquals(path, paths.ConfigFile)
                       || PathEquals(path, Stats.ResetMarker(paths))
                       || (PathEquals(Path.GetDirectoryName(path), paths.ListsDirectory)
                           && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        if (relevant) Signal();
    }

    private void ApplySystemDns(bool initial)
    {
        try
        {
            var changed = _systemDns!.Apply(_config.ExcludedAdapters, _servesIPv6);
            if (changed.Count > 0) log.Info($"DNS lookups on {string.Join(", ", changed)} now go through AdBlocker.");
            else if (initial) log.Warn("No connected network adapter was found. AdBlocker will switch adapters over when they connect.");
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (initial) throw new UserError($"Could not change the network settings: {e.Message}", e);
            log.Warn($"Could not switch a network adapter over: {e.Message}");
        }
    }

    private void RestoreSystemDns()
    {
        try
        {
            var restored = _systemDns!.Restore();
            if (restored.Count > 0) log.Info($"Restored the DNS settings of {string.Join(", ", restored)}.");
        }
        catch (Exception e)
        {
            log.Error($"Could not restore the DNS settings ({e.Message}). Run \"adblocker restore\" as administrator.");
        }
    }

    private void WarnUpstreamDown()
    {
        var now = DateTime.UtcNow;
        if (now - _lastUpstreamWarning < TimeSpan.FromMinutes(1)) return;
        _lastUpstreamWarning = now;
        log.Warn($"No DNS server answered ({string.Join(", ", _upstreams.Servers.Select(s => s.Name))}). Is the internet connection down?");
    }

    private static bool PathEquals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
