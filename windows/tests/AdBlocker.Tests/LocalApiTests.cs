using System.Net;
using System.Net.Sockets;
using System.Text;
using AdBlocker.Core;

namespace AdBlocker.Tests;

public class LocalApiTests : IAsyncLifetime
{
    private LocalApi _api = null!;
    private int Port => _api.Port;

    public Task InitializeAsync()
    {
        _api = LocalApi.TryStart(
                   0,
                   host => host is "ads.example.com" or "tracker.example.net" or "fake-shop.example",
                   host => host is "fake-shop.example",
                   new SilentLog())
               ?? throw new InvalidOperationException("Could not start the endpoint.");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Theory]
    [InlineData("ads.example.com", true, false)]
    [InlineData("ADS.Example.com.", true, false)]
    [InlineData("news.example.com", false, false)]
    [InlineData("fake-shop.example", true, true)]
    public async Task Answers_whether_a_host_is_blocked(string host, bool blocked, bool scam)
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{Port}/v1/check?host={Uri.EscapeDataString(host)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = (Blocked: blocked ? "true" : "false", Scam: scam ? "true" : "false");
        var expected = $$"""{"host":"{{host.ToLowerInvariant().TrimEnd('.')}}","blocked":{{json.Blocked}},"scam":{{json.Scam}}}""";
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reports_status_on_localhost_too()
    {
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://localhost:{Port}/v1/status");
        Assert.StartsWith("{\"app\":\"AdBlocker\",\"version\":\"", body);
    }

    [Theory]
    [InlineData("GET /v1/check?host=ads.example.com HTTP/1.1", "Host: evil.example:{port}", 403)]
    [InlineData("GET /v1/check?host=ads.example.com HTTP/1.1", "Host: 127.0.0.1:{port}\r\nOrigin: https://evil.example", 403)]
    [InlineData("GET /v1/check?host=ads.example.com HTTP/1.1", "Host: 127.0.0.1:{port}\r\nOrigin: chrome-extension://abcdef", 200)]
    [InlineData("POST /v1/check?host=ads.example.com HTTP/1.1", "Host: 127.0.0.1:{port}", 405)]
    [InlineData("GET /v1/check?host=not%20a%20host HTTP/1.1", "Host: 127.0.0.1:{port}", 400)]
    [InlineData("GET /v1/check HTTP/1.1", "Host: 127.0.0.1:{port}", 400)]
    [InlineData("GET /elsewhere HTTP/1.1", "Host: 127.0.0.1:{port}", 404)]
    [InlineData("nonsense", "Host: 127.0.0.1:{port}", 400)]
    public async Task Refuses_anything_unexpected(string requestLine, string headers, int status)
    {
        var request = $"{requestLine}\r\n{headers.Replace("{port}", Port.ToString())}\r\n\r\n";
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream);
        var statusLine = await reader.ReadLineAsync();
        Assert.StartsWith($"HTTP/1.1 {status} ", statusLine);
    }

    [Fact]
    public async Task Survives_clients_that_disconnect_early()
    {
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, Port);
            await client.GetStream().WriteAsync("GET /v1/sta"u8.ToArray());
        }
        using var http = new HttpClient();
        Assert.Contains("\"blocked\":true", await http.GetStringAsync($"http://127.0.0.1:{Port}/v1/check?host=tracker.example.net"));
    }

    [Fact]
    public async Task Tells_whether_the_endpoint_is_answering()
    {
        Assert.True(await LocalApi.IsAnsweringAsync(Port));
        Assert.False(await LocalApi.IsAnsweringAsync(TestDns.FreeTcpPort()));

        // Another program's web server on the port doesn't count.
        var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        try
        {
            var serving = Task.Run(async () =>
            {
                using var client = await other.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1024];
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray());
            });
            Assert.False(await LocalApi.IsAnsweringAsync(((IPEndPoint)other.LocalEndpoint).Port));
            await serving;
        }
        finally
        {
            other.Stop();
        }
    }

    [Fact]
    public void Reports_a_taken_port()
    {
        var log = new SilentLog();
        Assert.Null(LocalApi.TryStart(Port, _ => false, _ => false, log));
        Assert.Contains(log.Warnings, w => w.Contains($"port {Port}"));
    }

    private sealed class SilentLog : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message)
        {
        }

        public void Warn(string message) => Warnings.Add(message);

        public void Error(string message) => Warnings.Add(message);
    }
}
