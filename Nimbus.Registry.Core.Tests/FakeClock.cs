namespace Nimbus.Registry.Core.Tests;

/// <summary>Deterministic TimeProvider: starts at a fixed instant, moves only when told.
/// Lets the time-window tests advance the clock instead of forging timestamps in the
/// past or bending config windows negative.</summary>
public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;

    public long NowUnix => now.ToUnixTimeSeconds();
}
