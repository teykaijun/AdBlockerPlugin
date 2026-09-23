using System.Net;
using AdBlocker.Dns;

namespace AdBlocker.Tests;

public class LocalNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.3.4")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.23")]
    [InlineData("169.254.10.1")]
    [InlineData("100.101.102.103")] // Tailscale and other private overlays
    [InlineData("fe80::1")]
    [InlineData("fd00::1234")]
    [InlineData("::ffff:192.168.1.23")]
    public void Answers_devices_on_the_local_network(string address) =>
        Assert.True(LocalNetwork.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")] // just outside 172.16/12
    [InlineData("172.32.0.1")]
    [InlineData("192.167.1.1")]
    [InlineData("100.63.0.1")] // just outside 100.64/10
    [InlineData("100.128.0.1")]
    [InlineData("203.0.113.9")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Ignores_the_rest_of_the_internet(string address) =>
        Assert.False(LocalNetwork.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void Lists_only_private_addresses_of_this_PC()
    {
        // Whatever this machine has, none of it may be a public address people could be told to use.
        Assert.All(LocalNetwork.OwnAddresses(), address =>
        {
            Assert.True(LocalNetwork.IsPrivate(address));
            Assert.NotEqual(IPAddress.Loopback, address);
        });
    }
}
