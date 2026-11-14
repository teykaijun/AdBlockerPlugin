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
        Assert.All(LocalNetwork.LanAddresses(), a => Assert.False(string.IsNullOrEmpty(a.Adapter)));
    }

    [Theory]
    [InlineData("192.168.1.50", true, true)]
    [InlineData("10.0.0.7", true, true)]
    [InlineData("172.20.48.1", false, false)] // Hyper-V or WSL switch: no gateway
    [InlineData("192.168.56.1", false, false)] // VirtualBox host-only adapter
    [InlineData("100.101.102.103", false, true)] // Tailscale has no gateway, but phones on the tailnet can use it
    [InlineData("8.8.8.8", true, false)]
    [InlineData("127.0.0.1", true, false)]
    [InlineData("fd00::5", true, false)] // phones are given an IPv4 address to type in
    public void Offers_only_addresses_a_phone_can_reach(string address, bool adapterHasGateway, bool offered) =>
        Assert.Equal(offered, LocalNetwork.IsLanAddress(IPAddress.Parse(address), adapterHasGateway));

    [Fact]
    public void Names_the_adapter_of_each_address()
    {
        Assert.Equal("192.168.1.50 (Wi-Fi)", new LanAddress(IPAddress.Parse("192.168.1.50"), "Wi-Fi").ToString());
    }

    [Fact]
    public void Knows_this_PCs_own_addresses()
    {
        Assert.Contains(IPAddress.Loopback, LocalNetwork.ThisComputer());
        Assert.Equal(IPAddress.Parse("fe80::1"), LocalNetwork.Plain(IPAddress.Parse("fe80::1%12")));
        Assert.Equal(IPAddress.Parse("192.168.1.5"), LocalNetwork.Plain(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.Equal(IPAddress.Parse("192.168.1.5"), LocalNetwork.Plain(IPAddress.Parse("192.168.1.5")));
    }
}
