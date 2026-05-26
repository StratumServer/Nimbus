using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// PROXY protocol v2 header builder. Spec:
//   https://www.haproxy.org/download/2.8/doc/proxy-protocol.txt
//
// Header layout:
//   12 byte signature: 0x0D 0x0A 0x0D 0x0A 0x00 0x0D 0x0A 0x51 0x55 0x49 0x54 0x0A
//   1  byte version/cmd: 0x21 = v2 PROXY
//   1  byte family/transport: 0x11 = TCP/IPv4, 0x21 = TCP/IPv6
//   2  bytes addr length (big-endian uint16)
//   address block: TCP4 = 12 bytes, TCP6 = 36 bytes
//
// Always emits the PROXY command (0x21), never LOCAL, so the backend records the original
// client endpoint.
internal static class ProxyProtocolV2
{
    private static readonly byte[] Signature =
    {
        0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A
    };

    // Build a v2 header for a client -> upstream pair. If the two endpoints aren't the same
    // family after unwrapping v4-mapped-v6, both get promoted to v6 so we can still encode it.
    public static byte[] BuildHeader(IPEndPoint client, IPEndPoint upstream)
    {
        var src = Unmap(client.Address);
        var dst = Unmap(upstream.Address);

        if (src.AddressFamily != dst.AddressFamily)
        {
            if (src.AddressFamily == AddressFamily.InterNetwork) src = IPAddress.Parse("::ffff:" + src);
            if (dst.AddressFamily == AddressFamily.InterNetwork) dst = IPAddress.Parse("::ffff:" + dst);
        }

        bool v4 = src.AddressFamily == AddressFamily.InterNetwork;
        int addrLen = v4 ? 12 : 36;
        byte family = (byte)(v4 ? 0x11 : 0x21);

        var buf = new byte[16 + addrLen];
        Buffer.BlockCopy(Signature, 0, buf, 0, 12);
        buf[12] = 0x21;       // v2 PROXY
        buf[13] = family;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(14, 2), (ushort)addrLen);

        var srcBytes = src.GetAddressBytes();
        var dstBytes = dst.GetAddressBytes();
        int o = 16;
        Buffer.BlockCopy(srcBytes, 0, buf, o, srcBytes.Length); o += srcBytes.Length;
        Buffer.BlockCopy(dstBytes, 0, buf, o, dstBytes.Length); o += dstBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), (ushort)client.Port);   o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o, 2), (ushort)upstream.Port);

        return buf;
    }

    private static IPAddress Unmap(IPAddress a)
        => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
