using System.Net;
using AdBlocker.Dns;

namespace AdBlocker.Tests;

public class DnsMessageTests
{
    [Fact]
    public void Reads_the_question_in_lower_case()
    {
        var query = DnsMessage.BuildQuery(0xBEEF, "Ads.Example.COM", DnsMessage.TypeAaaa);
        Assert.True(DnsMessage.TryParseQuery(query, out var question));
        Assert.Equal("ads.example.com", question.Name);
        Assert.Equal(DnsMessage.TypeAaaa, question.Type);
        Assert.Equal(0xBEEF, question.Id);
        Assert.Equal(query.Length, question.End);
    }

    [Fact]
    public void Reads_the_root_name_as_empty()
    {
        Assert.True(DnsMessage.TryParseQuery(DnsMessage.BuildQuery(1, ".", DnsMessage.TypeA), out var question));
        Assert.Equal("", question.Name);
    }

    [Fact]
    public void Ignores_responses_other_opcodes_and_malformed_names()
    {
        var query = DnsMessage.BuildQuery(1, "example.com", DnsMessage.TypeA);

        var response = (byte[])query.Clone();
        response[2] |= 0x80;
        Assert.False(DnsMessage.TryParseQuery(response, out _));

        var notify = (byte[])query.Clone();
        notify[2] = 4 << 3;
        Assert.False(DnsMessage.TryParseQuery(notify, out _));

        var noQuestion = (byte[])query.Clone();
        noQuestion[5] = 0;
        Assert.False(DnsMessage.TryParseQuery(noQuestion, out _));

        var pointer = (byte[])query.Clone();
        pointer[12] = 0xC0;
        Assert.False(DnsMessage.TryParseQuery(pointer, out _));

        Assert.False(DnsMessage.TryParseQuery(query[..20], out _));
        Assert.False(DnsMessage.TryParseQuery(new byte[11], out _));
    }

    [Fact]
    public void Rejects_names_longer_than_253_characters()
    {
        var name = string.Join('.', Enumerable.Repeat(new string('a', 63), 4)); // 255 characters
        Assert.False(DnsMessage.TryParseQuery(DnsMessage.BuildQuery(1, name, DnsMessage.TypeA), out _));
    }

    [Fact]
    public void Blocked_response_is_NXDOMAIN_echoing_only_the_question()
    {
        var query = TestDns.QueryWithEdns("tracker.example.net", id: 0x0A0B);
        Assert.True(DnsMessage.TryParseQuery(query, out var question));
        var response = DnsMessage.BlockedResponse(query, question);

        Assert.Equal(question.End, response.Length); // the OPT record is gone
        Assert.Equal(0x0A0B, DnsMessage.ReadId(response));
        Assert.True(DnsMessage.IsResponse(response));
        Assert.False(DnsMessage.IsTruncated(response));
        Assert.Equal(DnsMessage.RcodeNxDomain, DnsMessage.ResponseCode(response));
        Assert.Equal(0x01, response[2] & 0x01); // RD copied
        Assert.Equal(0x80, response[3] & 0x80); // RA
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }, response[4..12]);
        Assert.Equal(query[12..question.End], response[12..]);
    }

    [Fact]
    public void Failure_and_truncated_responses()
    {
        var query = DnsMessage.BuildQuery(7, "example.com", DnsMessage.TypeA);
        Assert.True(DnsMessage.TryParseQuery(query, out var question));

        Assert.Equal(DnsMessage.RcodeServFail, DnsMessage.ResponseCode(DnsMessage.FailureResponse(query, question)));

        var truncated = DnsMessage.TruncatedResponse(query, question);
        Assert.True(DnsMessage.IsTruncated(truncated));
        Assert.Equal(0, DnsMessage.ResponseCode(truncated));
    }

    [Fact]
    public void Knows_how_big_a_UDP_answer_may_be()
    {
        var plain = DnsMessage.BuildQuery(1, "example.com", DnsMessage.TypeA);
        Assert.True(DnsMessage.TryParseQuery(plain, out var q1));
        Assert.Equal(512, DnsMessage.MaxUdpResponseSize(plain, q1));

        var edns = TestDns.QueryWithEdns("example.com", udpSize: 4096);
        Assert.True(DnsMessage.TryParseQuery(edns, out var q2));
        Assert.Equal(4096, DnsMessage.MaxUdpResponseSize(edns, q2));

        var tiny = TestDns.QueryWithEdns("example.com", udpSize: 100);
        Assert.True(DnsMessage.TryParseQuery(tiny, out var q3));
        Assert.Equal(512, DnsMessage.MaxUdpResponseSize(tiny, q3));
    }

    [Fact]
    public void Reads_addresses_from_an_answer()
    {
        var query = DnsMessage.BuildQuery(3, "dns.example", DnsMessage.TypeA);
        var answer = TestDns.Answer(query, "192.0.2.1", "198.51.100.2");
        Assert.Equal(new[] { IPAddress.Parse("192.0.2.1"), IPAddress.Parse("198.51.100.2") }, DnsMessage.ReadAddresses(answer));
        Assert.Empty(DnsMessage.ReadAddresses(answer[..20]));
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(28, "AAAA")]
    [InlineData(65, "HTTPS")]
    [InlineData(99, "TYPE99")]
    public void Names_record_types(ushort type, string name) => Assert.Equal(name, DnsMessage.TypeName(type));
}
