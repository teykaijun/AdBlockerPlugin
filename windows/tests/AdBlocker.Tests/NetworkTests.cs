using System.Net;
using AdBlocker.Config;
using AdBlocker.Core;
using AdBlocker.Dns;
using AdBlocker.Filtering;

namespace AdBlocker.Tests;

public class UpstreamTests
{
    private sealed class FakeServer(string name, Func<byte[], Task<byte[]>> answer) : IUpstream
    {
        public int Calls;

        public string Name => name;

        public Task<byte[]> QueryAsync(byte[] query, CancellationToken cancellationToken)
        {
            Calls++;
            return answer(query);
        }
    }

    private static readonly byte[] Query = DnsMessage.BuildQuery(9, "example.com", DnsMessage.TypeA);

    [Fact]
    public async Task Fails_over_and_remembers_the_server_that_answered()
    {
        var broken = new FakeServer("broken", _ => throw new IOException("down"));
        var working = new FakeServer("working", q => Task.FromResult(TestDns.Answer(q, "192.0.2.1")));
        var pool = new UpstreamPool([broken, working], TimeSpan.FromSeconds(1));

        Assert.NotNull(await pool.QueryAsync(Query, CancellationToken.None));
        Assert.NotNull(await pool.QueryAsync(Query, CancellationToken.None));
        Assert.Equal(1, broken.Calls);
        Assert.Equal(2, working.Calls);
    }

    [Fact]
    public async Task Gives_up_on_servers_that_hang()
    {
        var hanging = new FakeServer("hanging", async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return [];
        });
        var pool = new UpstreamPool([hanging], TimeSpan.FromMilliseconds(100));
        Assert.Null(await pool.QueryAsync(Query, CancellationToken.None));
        Assert.Null(await new UpstreamPool([], TimeSpan.FromSeconds(1)).QueryAsync(Query, CancellationToken.None));
    }

    [Fact]
    public async Task Stops_when_the_caller_cancels()
    {
        var hanging = new FakeServer("hanging", async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return [];
        });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var pool = new UpstreamPool([hanging], TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.QueryAsync(Query, cancel.Token));
    }

    [Fact]
    public async Task Queries_over_UDP()
    {
        await using var upstream = new FakeUpstream();
        var answer = await new UdpUpstream(upstream.Endpoint).QueryAsync(Query, CancellationToken.None);
        Assert.Equal(new[] { IPAddress.Parse("192.0.2.10") }, DnsMessage.ReadAddresses(answer));
        Assert.Equal((1, 0), (upstream.UdpQueries, upstream.TcpQueries));
    }

    [Fact]
    public async Task Retries_truncated_answers_over_TCP()
    {
        await using var upstream = new FakeUpstream(truncateUdp: true);
        var answer = await new UdpUpstream(upstream.Endpoint).QueryAsync(Query, CancellationToken.None);
        Assert.False(DnsMessage.IsTruncated(answer));
        Assert.Equal((1, 1), (upstream.UdpQueries, upstream.TcpQueries));
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData("Cloudflare", null)]
    [InlineData("9.9.9.9", null)]
    [InlineData("[2620:fe::fe]:53", null)]
    [InlineData("192.168.1.1:5353", null)]
    [InlineData("https://dns.example/dns-query", null)]
    [InlineData("http://dns.example/dns-query", "must use https://")]
    [InlineData("127.0.0.1", "cannot be used")]
    [InlineData("127.0.0.1:5353", null)]
    [InlineData("0.0.0.0", "cannot be used")]
    [InlineData("dns.example", "is not an IP address")]
    public void Validates_upstream_entries(string entry, string? problem)
    {
        var result = UpstreamSettings.Validate(entry);
        if (problem is null) Assert.Null(result);
        else Assert.Contains(problem, result);
    }

    [Fact]
    public void Builds_servers_from_settings()
    {
        using var http = DohUpstream.CreateClient();
        var automatic = new[] { IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.1") };

        var servers = UpstreamSettings.Create(["auto", "quad9", "10.0.0.53", "https://dns.example/q"], automatic, http);
        Assert.Equal(new[] { "192.168.1.1", "https://9.9.9.9/dns-query", "https://149.112.112.112/dns-query", "10.0.0.53", "https://dns.example/q" },
            servers.Select(s => s.Name));

        var fallback = UpstreamSettings.Create(["auto"], [], http);
        Assert.Equal(UpstreamSettings.Fallback, fallback.Select(s => s.Name));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fec0:0:0:ffff::1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void Ignores_addresses_that_are_not_real_DNS_servers(string address, bool usable) =>
        Assert.Equal(usable, UpstreamSettings.IsUsable(IPAddress.Parse(address)));
}

public class DnsServerTests
{
    private static async Task<(DnsServer Server, IPEndPoint Endpoint)> StartAsync(
        Func<byte[], Task<byte[]?>> handler,
        Func<IPAddress, bool>? acceptClient = null)
    {
        var server = new DnsServer([new IPEndPoint(IPAddress.Loopback, 0)], (query, _) => handler(query), acceptClient);
        server.Start();
        await Task.Yield();
        return (server, server.BoundEndpoints[0]);
    }

    [Fact]
    public async Task Ignores_clients_it_does_not_accept()
    {
        var asked = 0;
        var (server, endpoint) = await StartAsync(
            q =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<byte[]?>(TestDns.Answer(q, "192.0.2.7"));
            },
            acceptClient: _ => false);

        await using (server)
        {
            var query = DnsMessage.BuildQuery(1, "example.com", DnsMessage.TypeA);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestDns.QueryUdpAsync(endpoint, query));

            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(endpoint);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await DnsTcp.WriteMessageAsync(tcp.GetStream(), query, timeout.Token);
                // The connection is dropped, which arrives as either an empty read or a reset.
                Assert.Null(await DnsTcp.ReadMessageAsync(tcp.GetStream(), timeout.Token));
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
            {
            }
            Assert.Equal(0, asked);
        }
    }

    [Fact]
    public async Task Answers_over_UDP_and_TCP()
    {
        var (server, endpoint) = await StartAsync(q => Task.FromResult<byte[]?>(TestDns.Answer(q, "192.0.2.7")));
        await using (server)
        {
            var query = DnsMessage.BuildQuery(42, "example.com", DnsMessage.TypeA);
            foreach (var answer in new[] { await TestDns.QueryUdpAsync(endpoint, query), await TestDns.QueryTcpAsync(endpoint, query) })
            {
                Assert.Equal(42, DnsMessage.ReadId(answer));
                Assert.Equal(new[] { IPAddress.Parse("192.0.2.7") }, DnsMessage.ReadAddresses(answer));
            }
        }
    }

    [Fact]
    public async Task Truncates_answers_too_big_for_UDP()
    {
        var addresses = Enumerable.Range(1, 60).Select(i => $"192.0.2.{i}").ToArray(); // ~1 KB
        var (server, endpoint) = await StartAsync(q => Task.FromResult<byte[]?>(TestDns.Answer(q, addresses)));
        await using (server)
        {
            var plain = DnsMessage.BuildQuery(1, "big.example", DnsMessage.TypeA);
            var udp = await TestDns.QueryUdpAsync(endpoint, plain);
            Assert.True(DnsMessage.IsTruncated(udp));
            Assert.Empty(DnsMessage.ReadAddresses(udp));

            Assert.Equal(60, DnsMessage.ReadAddresses(await TestDns.QueryTcpAsync(endpoint, plain)).Count);

            var edns = TestDns.QueryWithEdns("big.example", udpSize: 4096);
            Assert.Equal(60, DnsMessage.ReadAddresses(await TestDns.QueryUdpAsync(endpoint, edns)).Count);
        }
    }

    [Fact]
    public async Task Keeps_serving_after_bad_packets()
    {
        var (server, endpoint) = await StartAsync(q => Task.FromResult(DnsMessage.TryParseQuery(q, out _) ? TestDns.Answer(q, "192.0.2.8") : null));
        await using (server)
        {
            using (var junk = new System.Net.Sockets.UdpClient())
            {
                await junk.SendAsync(new byte[] { 1, 2, 3 }, endpoint);
            }
            var answer = await TestDns.QueryUdpAsync(endpoint, DnsMessage.BuildQuery(5, "example.com", DnsMessage.TypeA));
            Assert.Equal(5, DnsMessage.ReadId(answer));
        }
    }
}

/// <summary>The whole blocker on a spare port, forwarding to a fake upstream. No system settings are touched.</summary>
public class BlockerTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Temporary();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Blocks_forwards_and_follows_config_changes()
    {
        await using var upstream = new FakeUpstream();
        ConfigStore.Update(_paths, c =>
        {
            c.Upstream = [upstream.Endpoint.ToString()];
            // No downloads during the test.
            c.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-normal", Enabled = false });
            c.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-popupads", Enabled = false });
            c.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-fake", Enabled = false });
        });

        var port = TestDns.FreePort();
        var server = new IPEndPoint(IPAddress.Loopback, port);
        var echoed = new List<QueryEvent>();
        var apiPort = TestDns.FreeTcpPort();
        var blocker = new Blocker(_paths, new BlockerOptions { Port = port, ApiPort = apiPort, ManageSystemDns = false, Echo = e => { lock (echoed) echoed.Add(e); } }, new TestLog());

        using var stopping = new CancellationTokenSource();
        var running = blocker.RunAsync(stopping.Token);
        await WaitUntilAsync(() => blocker.BlockedDomainCount > 0, running);

        using (var http = new HttpClient())
        {
            var check = await http.GetStringAsync($"http://127.0.0.1:{apiPort}/v1/check?host=pagead2.googlesyndication.com");
            Assert.Equal("""{"host":"pagead2.googlesyndication.com","blocked":true,"scam":false}""", check);
        }

        var blocked = await TestDns.QueryUdpAsync(server, DnsMessage.BuildQuery(1, "pagead2.googlesyndication.com", DnsMessage.TypeA));
        Assert.Equal(DnsMessage.RcodeNxDomain, DnsMessage.ResponseCode(blocked));

        var allowed = await TestDns.QueryUdpAsync(server, DnsMessage.BuildQuery(2, "example.com", DnsMessage.TypeA));
        Assert.Equal(0, DnsMessage.ResponseCode(allowed));
        Assert.Equal(new[] { IPAddress.Parse("192.0.2.10") }, DnsMessage.ReadAddresses(allowed));
        Assert.Equal(1, upstream.UdpQueries);

        // Blocking a domain through the config takes effect without a restart.
        ConfigStore.Update(_paths, c => c.BlockedDomains.Add("example.com"));
        await WaitUntilAsync(async () =>
        {
            var answer = await TestDns.QueryUdpAsync(server, DnsMessage.BuildQuery(3, "www.example.com", DnsMessage.TypeA));
            return DnsMessage.ResponseCode(answer) == DnsMessage.RcodeNxDomain;
        }, running);

        await stopping.CancelAsync();
        await running;

        var stats = Stats.Read(_paths.StatsFile)!;
        Assert.False(stats.Running);
        Assert.True(stats.Blocked >= 2);
        Assert.True(stats.Queries >= 3);
        Assert.Contains("BLOCKED  A      pagead2.googlesyndication.com", File.ReadAllText(_paths.QueryLog));
        lock (echoed) Assert.Contains(echoed, e => e.Blocked && e.Name == "pagead2.googlesyndication.com");
        lock (echoed) Assert.DoesNotContain(echoed, e => e.Name == "example.com"); // allowed lookups are not echoed without --verbose
    }

    [Fact]
    public async Task Answers_SERVFAIL_when_no_upstream_responds()
    {
        ConfigStore.Update(_paths, c =>
        {
            c.Upstream = [$"127.0.0.2:{TestDns.FreePort()}"]; // nothing listens there
            c.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-normal", Enabled = false });
            c.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-popupads", Enabled = false });
            c.CommunityLists.Add(new CommunityListSetting { Id = "hagezi-fake", Enabled = false });
        });
        var port = TestDns.FreePort();
        var log = new TestLog();
        var blocker = new Blocker(_paths, new BlockerOptions { Port = port, ApiPort = 0, ManageSystemDns = false }, log);
        using var stopping = new CancellationTokenSource();
        var running = blocker.RunAsync(stopping.Token);
        await WaitUntilAsync(() => blocker.BlockedDomainCount > 0, running);

        var answer = await TestDns.QueryUdpAsync(new IPEndPoint(IPAddress.Loopback, port), DnsMessage.BuildQuery(4, "example.com", DnsMessage.TypeA));
        Assert.Equal(DnsMessage.RcodeServFail, DnsMessage.ResponseCode(answer));
        Assert.Contains(log.Warnings, w => w.Contains("No DNS server answered"));

        await stopping.CancelAsync();
        await running;
    }

    [Fact]
    public async Task Reports_a_busy_port_clearly()
    {
        using var taken = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)taken.Client.LocalEndPoint!).Port;
        using var takenV6 = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, port));
        var blocker = new Blocker(_paths, new BlockerOptions { Port = port, ApiPort = 0, ManageSystemDns = false }, new TestLog());
        var error = await Assert.ThrowsAsync<UserError>(() => blocker.RunAsync(CancellationToken.None));
        Assert.Contains($"Port {port} is already in use", error.Message);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, Task running)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (running.IsCompleted) await running; // surface startup errors
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the blocker.");
            await Task.Delay(100);
        }
    }

    private static Task WaitUntilAsync(Func<bool> condition, Task running) => WaitUntilAsync(() => Task.FromResult(condition()), running);

    private sealed class TestLog : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
            lock (Warnings) Warnings.Add(message);
        }

        public void Error(string message) => Warn(message);
    }
}
