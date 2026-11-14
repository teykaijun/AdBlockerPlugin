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

    /// <summary>Port of the endpoint the browser companion extension asks; 0 turns it off.</summary>
    public int ApiPort { get; init; } = LocalApi.DefaultPort;

    /// <summary>Also answer other devices on the local network; null follows config.json.</summary>
    public bool? ListenOnLan { get; init; }
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
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly FilterLists _lists = new(paths);
    private readonly SystemDns? _systemDns = options.ManageSystemDns ? new SystemDns(paths) : null;
    private readonly HttpClient _dohClient = DohUpstream.CreateClient();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Stats _stats = new(paths.StatsFile);
    private readonly Lock _networkSettings = new();

    private volatile RuleSet _rules = RuleSet.Empty;
    private volatile UpstreamPool _upstreams = new([], UpstreamTimeout);
    private volatile AppConfig _config = new();
    private string _matcherInputs = "";
    private string _selfServersNoted = "";
    private QueryLog? _queryLog;
    private int _networkChanged;
    private int _configChanged;
    private int _listsChanged;
    private bool _servesIPv6;
    private bool _servingLan;
    private bool _restoredForGood;
    private DateTime _lastUpstreamWarning;

    /// <summary>The blocked domains currently loaded (for tests and status).</summary>
    public int BlockedDomainCount => _rules.Matcher.BlockedCount;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Refuses a settings folder someone else created. Nothing in it can be trusted then, not
        // even the DNS backup, so the network settings are left alone as well.
        paths.EnsureCreated();
        // Only one blocker may own the network settings. Failing here changes nothing either.
        using var instance = _systemDns is not null ? BlockerLock.Acquire(paths) : null;
        try
        {
            await ServeAsync(cancellationToken);
        }
        catch (Exception) when (_systemDns is not null)
        {
            // Starting failed, perhaps after an unclean stop left the adapters pointing here.
            // Nothing would answer them then, and the PC would have no DNS at all.
            RestoreSystemDns(final: true);
            throw;
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.ConfigFile)) ConfigStore.Save(paths, new AppConfig()); // so people can see and edit the defaults

        await using var queryLog = _queryLog = new QueryLog(paths.QueryLog, options.Echo);
        Reload(initial: true);

        // Serving the network means binding every address, so only private clients are answered.
        var onLan = options.ListenOnLan ?? _config.ListenOnLan;
        _servingLan = onLan;
        IPEndPoint[] endpoints = onLan
            ? [new(IPAddress.Any, options.Port), new(IPAddress.IPv6Any, options.Port)]
            : [new(IPAddress.Loopback, options.Port), new(IPAddress.IPv6Loopback, options.Port)];
        await using var server = new DnsServer(endpoints, HandleAsync, onLan ? LocalNetwork.IsPrivate : null);
        Start(server);
        if (onLan)
        {
            var own = string.Join(", ", LocalNetwork.LanAddresses());
            log.Info($"Other devices on the local network can use this PC for DNS{(own.Length > 0 ? $": {own}" : "")}.");
        }
        _servesIPv6 = server.BoundEndpoints.Any(e => e.AddressFamily == AddressFamily.InterNetworkV6);
        log.Info($"Listening for DNS lookups on {string.Join(" and ", server.BoundEndpoints)}.");

        await using var api = options.ApiPort > 0 ? LocalApi.TryStart(options.ApiPort, IsBlocked, IsScam, log, () => PausedUntil) : null;
        if (api is not null) log.Info($"Answering the browser companion extension on 127.0.0.1:{api.Port}.");

        using var configWatcher = Watch(paths.Root);
        using var listWatcher = Watch(paths.ListsDirectory);
        NetworkAddressChangedEventHandler onNetworkChange = (_, _) => Signal(ref _networkChanged);
        NetworkChange.NetworkAddressChanged += onNetworkChange;
        try
        {
            if (_systemDns is not null) ApplySystemDns(initial: true);
            _stats.Save(running: true);
            await RunLoopsAsync(
                [
                    ("Following config, list and network changes", ProcessChangesAsync),
                    ("Updating the lists", RefreshListsAsync),
                    ("Saving the statistics", SaveStatsAsync),
                ],
                cancellationToken);
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onNetworkChange;
            if (_systemDns is not null) RestoreSystemDns(final: true);
            _stats.Save(running: false);
        }
    }

    private void Start(DnsServer server)
    {
        try
        {
            server.Start();
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw PortInUse(e);
        }
        foreach (var (endpoint, error) in server.Failed) log.Warn($"Not listening on {endpoint}: {error.Message}");
        // IPv6 is optional, IPv4 is not: Windows and other devices send their lookups there, so
        // without it AdBlocker would only look as if it worked.
        if (!server.BoundEndpoints.Any(e => e.AddressFamily == AddressFamily.InterNetwork))
        {
            throw PortInUse(server.Failed.FirstOrDefault(f => f.Endpoint.AddressFamily == AddressFamily.InterNetwork).Error);
        }
    }

    private UserError PortInUse(Exception? inner) => new(
        $"Port {options.Port} is already in use, so AdBlocker cannot receive DNS lookups. " +
        "Another DNS program, or Windows' Mobile hotspot / Internet Connection Sharing, may be using it.", inner);

    /// <summary>
    /// Runs the background loops until <paramref name="cancellationToken"/> is cancelled. A loop
    /// that ends by itself is a failure, whatever the reason (a timeout ends a loop as "cancelled"
    /// too), so the caller and the service can report it. Every loop has finished when this
    /// returns, so none of them is still changing the network settings while they are restored.
    /// </summary>
    internal static async Task RunLoopsAsync(IReadOnlyList<(string Name, Func<CancellationToken, Task> Run)> loops, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = loops.Select(l => l.Run(stopping.Token)).ToArray();
        var first = await Task.WhenAny(tasks);
        await stopping.CancelAsync();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // Only that they all ended matters here; what ended them is looked at below.
        }
        if (cancellationToken.IsCancellationRequested) return;

        var name = loops[Array.IndexOf(tasks, first)].Name;
        if (first.Exception?.GetBaseException() is { } error) throw new InvalidOperationException($"{name} failed: {error.Message}", error);
        throw new InvalidOperationException($"{name} stopped unexpectedly.");
    }

    private async Task<byte[]?> HandleAsync(byte[] query, CancellationToken cancellationToken)
    {
        if (!DnsMessage.TryParseQuery(query, out var question)) return null;

        var now = DateTimeOffset.Now;
        var rules = _rules;
        var blocked = rules.IsBlocked(question.Name, now);
        _stats.Record(blocked);
        _queryLog?.Add(
            new QueryEvent(now, question.Name, question.Type, blocked, blocked && rules.Scam.IsBlocked(question.Name)),
            toFile: blocked || _config.LogAllowedQueries,
            toEcho: blocked || options.Verbose);
        if (blocked) return DnsMessage.BlockedResponse(query, question);

        var answer = await _upstreams.QueryAsync(query, cancellationToken);
        if (answer is null)
        {
            WarnUpstreamDown();
            return DnsMessage.FailureResponse(query, question);
        }
        // The servers check what they get back, but a reply too short to hold an id must never pass.
        if (answer.Length < DnsMessage.HeaderSize) return DnsMessage.FailureResponse(query, question);
        DnsMessage.WriteId(answer, question.Id);
        return answer;
    }

    /// <summary>
    /// Whether a lookup for <paramref name="host"/> is blocked right now: the user's own rules,
    /// temporary allowances, a pause and the lists, in that order (see <see cref="RuleSet"/>).
    /// DNS answers and the browser companion both ask this.
    /// </summary>
    public bool IsBlocked(string host) => _rules.IsBlocked(host, DateTimeOffset.Now);

    /// <summary>Whether a blocked host is on one of the scam lists, so it is worth warning about.</summary>
    public bool IsScam(string host) => _rules.IsScam(host, DateTimeOffset.Now);

    /// <summary>When "adblocker pause" ends, or null when blocking.</summary>
    public DateTimeOffset? PausedUntil => _rules.PausedUntil(DateTimeOffset.Now);

    /// <summary>
    /// Reads config.json again. The big matchers are only rebuilt when what they are made of
    /// changed, or <paramref name="rebuildLists"/> says a list file did, so a pause or an
    /// allowance takes effect without parsing every list again. Everything is ready before
    /// anything is swapped in, so a failure leaves the previous state whole.
    /// </summary>
    private void Reload(bool initial, bool rebuildLists = false)
    {
        var config = ConfigStore.Load(paths);
        // The first load comes before the sockets open; after that, they serve what they started with.
        var self = LocalNetwork.AnsweredByAdBlocker(initial ? options.ListenOnLan ?? config.ListenOnLan : _servingLan);
        foreach (var entry in config.Upstream)
        {
            if (UpstreamSettings.Validate(entry, self) is { } problem) log.Warn($"Ignoring DNS server {problem}");
        }

        var previous = _rules;
        var inputs = FilterLists.MatcherInputs(config);
        var full = initial || rebuildLists || inputs != _matcherInputs;
        var matcher = full ? _lists.BuildMatcher(config) : previous.Matcher;
        var scam = full ? _lists.BuildScamMatcher(config) : previous.Scam;
        var rules = new RuleSet(matcher, scam, new TemporaryAllowances(config.TemporarilyAllowed), config.PausedUntil);
        var upstreams = BuildUpstreams(config, self, out var selfServers);

        // Said once, not again at every pause or allowance.
        var selfNote = string.Join(", ", selfServers);
        if (selfNote.Length > 0 && selfNote != _selfServersNoted)
        {
            log.Warn($"Not sending lookups to {selfNote}: that is this PC itself (perhaps the router hands it out as the DNS server).");
        }
        // The sockets are opened once, so this one only takes effect after a restart.
        if (!initial && options.ListenOnLan is null && config.ListenOnLan != _servingLan)
        {
            log.Warn($"Serving the local network is now {(config.ListenOnLan ? "on" : "off")} in config.json. " +
                     "Run \"adblocker restart\" to apply it.");
        }

        _config = config;
        _rules = rules;
        _upstreams = upstreams;
        _matcherInputs = inputs;
        _selfServersNoted = selfNote;

        var servers = string.Join(", ", upstreams.Servers.Select(s => s.Name));
        log.Info(full
            ? $"{(initial ? "Loaded" : "Reloaded")} {matcher.BlockedCount:N0} blocked domains " +
              $"({scam.BlockedCount:N0} of them known scam sites). Allowed lookups go to {servers}."
            : $"Applied the new settings; the lists stay as they were. Allowed lookups go to {servers}.");
        NotePause(initial ? null : previous, rules);
    }

    /// <param name="selfServers">Servers "auto" found that are this PC itself, and so left out.</param>
    private UpstreamPool BuildUpstreams(AppConfig config, IReadOnlySet<IPAddress> self, out List<IPAddress> selfServers)
    {
        var automatic = _systemDns?.OriginalServers(config.ExcludedAdapters) ?? SystemDns.CurrentServers(config.ExcludedAdapters);
        selfServers = config.Upstream.Contains(UpstreamSettings.Auto, StringComparer.OrdinalIgnoreCase)
            ? automatic.Where(a => self.Contains(LocalNetwork.Plain(a))).ToList()
            : [];
        var servers = UpstreamSettings.Create(config.Upstream, automatic, _dohClient, self);
        // The same servers keep the same pool, and with it the server it learned to try first.
        var current = _upstreams;
        return current.Servers.Select(s => s.Name).SequenceEqual(servers.Select(s => s.Name))
            ? current
            : new UpstreamPool(servers, UpstreamTimeout);
    }

    /// <summary>After a network change only the servers "auto" finds can have changed.</summary>
    private void RefreshUpstreams()
    {
        var upstreams = BuildUpstreams(_config, LocalNetwork.AnsweredByAdBlocker(_servingLan), out _);
        if (upstreams == _upstreams) return;
        _upstreams = upstreams;
        log.Info($"Allowed lookups now go to {string.Join(", ", upstreams.Servers.Select(s => s.Name))}.");
    }

    /// <summary>Notes in the log when a pause starts, or ends before its time.</summary>
    private void NotePause(RuleSet? previous, RuleSet current)
    {
        var now = DateTimeOffset.Now;
        var was = previous?.PausedUntil(now);
        var paused = current.PausedUntil(now);
        if (paused is { } until && until != was) log.Info($"Blocking is paused until {until.LocalDateTime:HH:mm}.");
        else if (paused is null && was is not null) log.Info("Blocking again.");
    }

    private void Signal(ref int flag)
    {
        Interlocked.Exchange(ref flag, 1);
        Signal();
    }

    private void Signal() => _changes.Writer.TryWrite(true);

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        while (await _changes.Reader.WaitToReadAsync(cancellationToken))
        {
            // Let a burst of file or network events settle first.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            while (_changes.Reader.TryRead(out _))
            {
            }

            var network = Interlocked.Exchange(ref _networkChanged, 0) == 1;
            var config = Interlocked.Exchange(ref _configChanged, 0) == 1;
            var lists = Interlocked.Exchange(ref _listsChanged, 0) == 1;
            var exclusions = _config.ExcludedAdapters;
            ResetStatsIfAsked();
            try
            {
                if (config || lists)
                {
                    Reload(initial: false, rebuildLists: lists);
                    // Windows' DNS cache still holds answers the new rules may decide differently.
                    if (_systemDns is not null) SystemDns.FlushCache();
                }
                else if (network)
                {
                    RefreshUpstreams();
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // The previous settings stay in force; the list files are looked at again next time.
                if (lists) Interlocked.Exchange(ref _listsChanged, 1);
                log.Warn($"Could not apply the change: {e.Message}");
            }

            if (_systemDns is null) continue;
            try
            {
                var excludedChanged = !exclusions.SequenceEqual(_config.ExcludedAdapters, StringComparer.OrdinalIgnoreCase);
                // A newly excluded adapter gets its own settings back before the others are switched again.
                if (excludedChanged) RestoreSystemDns(final: false);
                if (network || excludedChanged) ApplySystemDns(initial: false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                log.Warn($"Could not switch the network adapters over: {e.Message}");
            }
        }
    }

    private void ResetStatsIfAsked()
    {
        var marker = Stats.ResetMarker(paths);
        if (!File.Exists(marker)) return;
        _stats.Reset();
        try
        {
            File.Delete(marker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Then the counters are reset once more next time, which does no harm.
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
            // A timeout is an OperationCanceledException too; only the token means "stop".
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                log.Warn($"Updating community lists failed: {e.Message}");
            }
            await CheckForUpdateAsync(http, cancellationToken);
            await Task.Delay(ListRefreshInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Once a day, notes whether a newer AdBlocker was released, so "adblocker status" can say
    /// so without going online itself. Nothing is installed without the user asking.
    /// </summary>
    private async Task CheckForUpdateAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (!_config.CheckForUpdates) return;
        try
        {
            var last = Updates.Read(paths)?.CheckedAt ?? DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - last < UpdateCheckInterval) return;
            var release = await Updates.CheckAsync(http, AppInfo.Version, cancellationToken);
            Updates.Save(paths, release);
            if (release is not null) log.Info($"AdBlocker {release.Version} is available. Run \"adblocker upgrade\" to install it.");
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            log.Info($"Could not check for a new version: {e.Message}");
        }
    }

    private async Task SaveStatsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var checkedAt = DateTimeOffset.Now;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _stats.Save(running: true);
            var now = DateTimeOffset.Now;
            NoteWhatEnded(checkedAt, now);
            checkedAt = now;
        }
    }

    /// <summary>
    /// A pause or an allowance runs out without anything being written, so this looks for that
    /// every few seconds. Windows' DNS cache still holds what was looked up in the meantime, so
    /// it is flushed, or blocking would only seem to start again minutes later.
    /// </summary>
    private void NoteWhatEnded(DateTimeOffset since, DateTimeOffset now)
    {
        var rules = _rules;
        var pause = rules.PauseEndedBetween(since, now);
        var allowances = rules.Allowances.EndedBetween(since, now);
        if (pause) log.Info("The pause ended; blocking again.");
        foreach (var domain in allowances) log.Info($"The allowance for {domain} ended.");
        if ((pause || allowances.Count > 0) && _systemDns is not null) SystemDns.FlushCache();
    }

    private FileSystemWatcher Watch(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
        watcher.Created += (_, e) => OnFileEvent(e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
        // Events were lost (too many at once), so everything is read again.
        watcher.Error += (_, _) =>
        {
            Interlocked.Exchange(ref _listsChanged, 1);
            Signal(ref _configChanged);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void OnFileEvent(string path)
    {
        if (PathEquals(path, paths.ConfigFile)) Signal(ref _configChanged);
        else if (PathEquals(path, Stats.ResetMarker(paths))) Signal();
        else if (PathEquals(Path.GetDirectoryName(path), paths.ListsDirectory) && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) Signal(ref _listsChanged);
    }

    private void ApplySystemDns(bool initial)
    {
        // One at a time with restoring, so the two never undo each other half-way.
        lock (_networkSettings)
        {
            // Once stopping has restored the settings, nothing may point the adapters here again.
            if (_restoredForGood) return;
            try
            {
                var result = _systemDns!.Apply(_config.ExcludedAdapters, _servesIPv6);
                foreach (var (adapter, error) in result.Failed) log.Warn($"Could not switch {adapter} over to AdBlocker: {error}");
                if (result.Changed.Count > 0) log.Info($"DNS lookups on {string.Join(", ", result.Changed)} now go through AdBlocker.");
                else if (initial && result.Adapters == 0) log.Warn("No connected network adapter was found. AdBlocker will switch adapters over when they connect.");
                // One adapter refusing is no reason not to protect the others; all of them refusing is.
                if (initial && result.NothingSwitched)
                {
                    throw new UserError($"Could not change the network settings: {result.Failed[0].Error}");
                }
            }
            catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                if (initial) throw new UserError($"Could not change the network settings: {e.Message}", e);
                log.Warn($"Could not switch a network adapter over: {e.Message}");
            }
        }
    }

    /// <param name="final">Stopping: the adapters must not be switched over again afterwards.</param>
    private void RestoreSystemDns(bool final)
    {
        lock (_networkSettings)
        {
            if (final) _restoredForGood = true;
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
