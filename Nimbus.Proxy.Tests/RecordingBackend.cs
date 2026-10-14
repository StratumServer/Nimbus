using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// A stand-in backend on an ephemeral port that records every connection it accepts and every
/// byte it is sent. Shared by the socket-level session tests.
/// </summary>
internal sealed class RecordingBackend : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly List<byte> received = new();
    private readonly List<TcpClient> accepted = new();
    private readonly object gate = new();
    private int connections;
    private long bytesSent;
    private volatile bool sending;
    private volatile bool reading = true;

    public RecordingBackend()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int BytesReceived { get { lock (gate) return received.Count; } }

    /// <summary>Connections accepted so far. The only signal available when a session is routed
    /// here but has not sent anything yet.</summary>
    public int Connections => Volatile.Read(ref connections);

    /// <summary>Bytes handed to the sockets by <see cref="KeepSending"/>. It stops growing once
    /// the proxy's s->c pump is parked inside a write to a client that is not reading, which is
    /// the state the write-failure tests have to reach before they reset anything.</summary>
    public long BytesSent => Interlocked.Read(ref bytesSent);

    public BackendEndpoint Endpoint(string serverId = "")
        => new() { Host = "127.0.0.1", Port = Port, ServerId = serverId };

    public bool Sent(string needle)
    {
        lock (gate) return Encoding.UTF8.GetString(received.ToArray()).Contains(needle);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref connections);
                lock (gate) accepted.Add(client);
                _ = Task.Run(() => ReadLoopAsync(client));
                if (sending) StartSender(client);
            }
        }
        catch { }
    }

    /// <summary>Stops draining what the sessions send, leaving the bytes to fill the socket
    /// buffers and back the proxy's c->s pump up into its write. A backend that is alive but
    /// wedged behaves this way, and it is the only way to park that pump on a write.</summary>
    public void StopReading() => reading = false;

    private async Task ReadLoopAsync(TcpClient client)
    {
        var buf = new byte[4096];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (!reading)
                {
                    await Task.Delay(20, cts.Token).ConfigureAwait(false);
                    continue;
                }
                int read = await client.GetStream().ReadAsync(buf, cts.Token).ConfigureAwait(false);
                if (read <= 0) return;
                lock (gate) received.AddRange(buf.AsSpan(0, read).ToArray());
            }
        }
        catch { }
    }

    /// <summary>Closes every connection accepted so far, leaving the listener up. This is what a
    /// backend process dying under a live session looks like from the proxy's side, which
    /// Dispose does not reproduce: cancelling the read loop leaves the socket open.</summary>
    public void DropConnections()
    {
        lock (gate)
        {
            foreach (var c in accepted) { try { c.Close(); } catch { /* already gone */ } }
            accepted.Clear();
        }
    }

    /// <summary>Resets every accepted connection instead of hanging up on it politely, so the
    /// proxy meets a failed write rather than a clean end of stream. A backend process being
    /// killed looks like this on the wire, and it is the only way to reach the write half of the
    /// pump exit paths.</summary>
    public void AbortConnections()
    {
        lock (gate)
        {
            foreach (var c in accepted)
            {
                try
                {
                    c.LingerState = new LingerOption(true, 0);
                    c.Close();
                }
                catch { /* already gone; the reset it would have sent is moot */ }
            }
            accepted.Clear();
        }
    }

    /// <summary>Starts a continuous stream of EntityPosition frames to every connection, now and
    /// for any accepted later. A backend talks to a player who is in the world without being
    /// asked, and the s->c pump only has a write to fail on while that is happening.</summary>
    public void KeepSending()
    {
        sending = true;
        lock (gate) foreach (var c in accepted) StartSender(c);
    }

    private void StartSender(TcpClient client)
    {
        _ = Task.Run(async () =>
        {
            var frame = ServerFrames.EntityPosition(16 * 1024);
            try
            {
                while (sending && !cts.IsCancellationRequested)
                {
                    await client.GetStream().WriteAsync(frame, cts.Token).ConfigureAwait(false);
                    Interlocked.Add(ref bytesSent, frame.Length);
                }
            }
            catch { /* the session it was feeding is gone, which is what the test arranged */ }
        });
    }

    public void Dispose()
    {
        sending = false;
        cts.Cancel();
        try { listener.Stop(); } catch { }
        cts.Dispose();
    }
}
