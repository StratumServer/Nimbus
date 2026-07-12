using System.Net;
using Xunit;

namespace Nimbus.Proxy.Tests;

public class ProxyProtocolV2Tests
{
    private static readonly byte[] Signature =
    {
        0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A
    };

    private static IPEndPoint Ep(string addr, int port) => new(IPAddress.Parse(addr), port);

    [Fact]
    public void Ipv4Pair_ProducesA28ByteTcp4Header()
    {
        byte[] h = ProxyProtocolV2.BuildHeader(Ep("192.0.2.10", 51234), Ep("10.0.0.1", 42421));

        Assert.Equal(28, h.Length);
        Assert.Equal(Signature, h[..12]);
        Assert.Equal(0x21, h[12]);                        // v2, PROXY command (never LOCAL)
        Assert.Equal(0x11, h[13]);                        // TCP over IPv4
        Assert.Equal(12, (h[14] << 8) | h[15]);           // address block length
        Assert.Equal(new byte[] { 192, 0, 2, 10 }, h[16..20]);
        Assert.Equal(new byte[] { 10, 0, 0, 1 }, h[20..24]);
        Assert.Equal(51234, (h[24] << 8) | h[25]);        // src port, big-endian
        Assert.Equal(42421, (h[26] << 8) | h[27]);        // dst port
    }

    [Fact]
    public void Ipv6Pair_ProducesA52ByteTcp6Header()
    {
        byte[] h = ProxyProtocolV2.BuildHeader(Ep("2001:db8::1", 1000), Ep("2001:db8::2", 2000));

        Assert.Equal(52, h.Length);
        Assert.Equal(0x21, h[13]);                        // TCP over IPv6
        Assert.Equal(36, (h[14] << 8) | h[15]);
        Assert.Equal(IPAddress.Parse("2001:db8::1").GetAddressBytes(), h[16..32]);
        Assert.Equal(IPAddress.Parse("2001:db8::2").GetAddressBytes(), h[32..48]);
        Assert.Equal(1000, (h[48] << 8) | h[49]);
        Assert.Equal(2000, (h[50] << 8) | h[51]);
    }

    [Fact]
    public void Ipv4MappedIpv6Client_IsUnwrapped_ToATcp4Header()
    {
        // What Socket.RemoteEndPoint reports on a dual-stack listener.
        byte[] h = ProxyProtocolV2.BuildHeader(Ep("::ffff:192.0.2.10", 51234), Ep("10.0.0.1", 42421));

        Assert.Equal(0x11, h[13]);
        Assert.Equal(new byte[] { 192, 0, 2, 10 }, h[16..20]);
    }

    [Fact]
    public void MixedFamilies_ArePromotedToIpv6()
    {
        byte[] h = ProxyProtocolV2.BuildHeader(Ep("192.0.2.10", 51234), Ep("2001:db8::2", 2000));

        Assert.Equal(52, h.Length);
        Assert.Equal(0x21, h[13]);
        Assert.Equal(IPAddress.Parse("::ffff:192.0.2.10").GetAddressBytes(), h[16..32]);
    }
}
