namespace Nimbus.Proxy;

internal sealed class StickyRouteSweeper
{
    private readonly StickyRouteTable stickies;
    private readonly CancellationToken stopToken;

    public StickyRouteSweeper(StickyRouteTable stickies, CancellationToken stopToken)
    {
        this.stickies = stickies;
        this.stopToken = stopToken;
    }

    public async Task RunAsync()
    {
        while (!stopToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            int dropped = stickies.SweepExpired();
            if (dropped > 0) Log.Trace($"sticky sweep: dropped {dropped} expired entries");
        }
    }
}
