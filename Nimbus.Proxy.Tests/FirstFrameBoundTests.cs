using System.Diagnostics;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// A not-yet-identified client picks the length of the allocation TryReadFirstFrameAsync makes
/// for its first frame, just by writing four header bytes. These tests pin the bound that keeps
/// that allocation small before any of the declared bytes have actually arrived, without
/// disturbing the normal first-frame paths (Identification, the status query, a quiet client).
/// </summary>
public class FirstFrameBoundTests
{
    private const int GenerousTimeoutMs = 5000;

    [Fact]
    public async Task ClientThatOnlyDeclaresAHugeLength_IsDisconnectedWithoutDialingABackend()
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Status.QueryTimeoutMs = GenerousTimeoutMs);

        var sw = Stopwatch.StartNew();
        await harness.SendAsync(HeaderDeclaring(200 * 1024 * 1024));
        await SessionHarness.WaitForAsync(() => harness.Running.IsCompleted,
            "the session kept running instead of closing on the over-bound declared length");
        sw.Stop();

        // The whole point of checking before allocating is that this peer never gets to make the
        // proxy wait out its query timeout for bytes that were never coming.
        Assert.True(sw.ElapsedMilliseconds < GenerousTimeoutMs / 2,
            $"rejection took {sw.ElapsedMilliseconds}ms, close to the {GenerousTimeoutMs}ms query timeout");
        Assert.Equal(0, harness.Backends["hub"].Connections);
    }

    [Fact]
    public async Task DeclaredLengthAtTheBound_IsStillAcceptedAsAFirstFrame()
    {
        using var harness = await SessionHarness.StartAsync();
        var backend = harness.Backends["hub"];

        var body = new byte[ClientSessionRunner.MaxFirstFrameSize];
        await harness.SendAsync(Frame(body));

        await SessionHarness.WaitForAsync(() => backend.BytesReceived >= 4 + body.Length,
            "the at-bound first frame never reached the backend");
        Assert.Equal(1, backend.Connections);
    }

    [Fact]
    public async Task DeclaredLengthOneOverTheBound_IsRefusedBeforeAllocating()
    {
        using var harness = await SessionHarness.StartAsync(cfg => cfg.Status.QueryTimeoutMs = GenerousTimeoutMs);

        await harness.SendAsync(HeaderDeclaring(ClientSessionRunner.MaxFirstFrameSize + 1));
        await SessionHarness.WaitForAsync(() => harness.Running.IsCompleted,
            "the session kept running instead of closing one byte over the bound");

        Assert.Equal(0, harness.Backends["hub"].Connections);
    }

    private static byte[] HeaderDeclaring(int len)
        => new byte[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };

    private static byte[] Frame(byte[] body)
    {
        var header = HeaderDeclaring(body.Length);
        var frame = new byte[4 + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, 4);
        return frame;
    }
}
