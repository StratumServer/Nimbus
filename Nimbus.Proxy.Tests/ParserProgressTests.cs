using Xunit;

namespace Nimbus.Proxy.Tests;

public class ParserProgressTests
{
    [Fact]
    public void VarintRejectsBitsBeyondUInt64()
    {
        int pos = 0;
        Assert.False(VsWire.TryReadVarint([0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2], ref pos, out _));
    }

    [Fact]
    public void MaximumWidthVarintRemainsValid()
    {
        int pos = 0;
        Assert.True(VsWire.TryReadVarint([255, 255, 255, 255, 255, 255, 255, 255, 255, 1], ref pos, out ulong value));
        Assert.Equal(ulong.MaxValue, value);
        Assert.Equal(10, pos);
    }

    [Fact(Timeout = 5000)]
    public async Task HistoricalCursorRewindAndFuzzTerminate()
    {
        await Task.Run(() => ExerciseInputs()).WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static void ExerciseInputs()
    {
        Assert.False(VsWire.TryFindNestedField([0x0a, 0xfa, 0xff, 0xff, 0xff, 0x0f], 2, out _));
        var random = new Random(1701);
        for (int i = 0; i < 10000; i++)
        {
            byte[] input = new byte[random.Next(128)];
            random.NextBytes(input);
            _ = VsWire.TryFindNestedField(input, 2, out _);
            _ = IdentificationParser.TryExtract(VsWire.WrapFrame(input), out _, out _);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void InvalidCursorIsRejected(int pos)
    {
        Assert.False(VsWire.TryReadVarint([1, 2, 3], ref pos, out _));
        Assert.False(VsWire.SkipField([1, 2, 3], ref pos, 1));
    }
}
