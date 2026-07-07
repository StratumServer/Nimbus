using System.Text;
using Nimbus.Shared;
using Nimbus.Shared.Security;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class HmacSignerTests
{
    private const string Secret = "unit-test-secret";

    private static string Canonical(string path = "/api/heartbeat", long ts = 1_750_000_000,
        string nonce = "n-1", string body = "{}")
        => HmacSigner.CanonicalString("POST", path, NimbusProtocol.ProtocolVersion, ts, nonce,
            Encoding.UTF8.GetBytes(body));

    [Fact]
    public void Sign_Verify_RoundTrips()
    {
        long ts = 1_750_000_000;
        string canonical = Canonical(ts: ts);
        string sig = HmacSigner.Sign(Secret, canonical);

        Assert.True(HmacSigner.Verify(Secret, canonical, sig, ts, nowUnix: ts));
    }

    [Fact]
    public void Verify_RejectsWrongSecret()
    {
        long ts = 1_750_000_000;
        string canonical = Canonical(ts: ts);
        string sig = HmacSigner.Sign(Secret, canonical);

        Assert.False(HmacSigner.Verify("other-secret", canonical, sig, ts, nowUnix: ts));
    }

    [Fact]
    public void Verify_RejectsTamperedSignature()
    {
        long ts = 1_750_000_000;
        string canonical = Canonical(ts: ts);
        char[] sig = HmacSigner.Sign(Secret, canonical).ToCharArray();
        sig[0] = sig[0] == 'A' ? 'B' : 'A';

        Assert.False(HmacSigner.Verify(Secret, canonical, new string(sig), ts, nowUnix: ts));
    }

    [Fact]
    public void Verify_RejectsOutsideClockSkew_EvenWithValidSignature()
    {
        long ts = 1_750_000_000;
        string canonical = Canonical(ts: ts);
        string sig = HmacSigner.Sign(Secret, canonical);

        long justInside = ts + NimbusProtocol.MaxClockSkewSeconds;
        long justOutside = ts + NimbusProtocol.MaxClockSkewSeconds + 1;
        Assert.True(HmacSigner.Verify(Secret, canonical, sig, ts, nowUnix: justInside));
        Assert.False(HmacSigner.Verify(Secret, canonical, sig, ts, nowUnix: justOutside));
        Assert.False(HmacSigner.Verify(Secret, canonical, sig, ts, nowUnix: ts - NimbusProtocol.MaxClockSkewSeconds - 1));
    }

    [Fact]
    public void CanonicalString_BindsEveryComponent()
    {
        string baseline = Canonical();

        Assert.NotEqual(baseline, Canonical(path: "/api/servers"));
        Assert.NotEqual(baseline, Canonical(ts: 1_750_000_001));
        Assert.NotEqual(baseline, Canonical(nonce: "n-2"));
        Assert.NotEqual(baseline, Canonical(body: "{\"a\":1}"));
        Assert.NotEqual(
            HmacSigner.CanonicalString("GET", "/api/heartbeat", NimbusProtocol.ProtocolVersion,
                1_750_000_000, "n-1", Encoding.UTF8.GetBytes("{}")),
            baseline);
    }

    [Fact]
    public void NewNonce_IsUrlSafeAndUnique()
    {
        var nonces = Enumerable.Range(0, 1000).Select(_ => HmacSigner.NewNonce()).ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
        Assert.All(nonces, n => Assert.Matches("^[A-Za-z0-9_-]+$", n));
    }
}
