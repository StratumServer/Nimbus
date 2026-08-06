using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// A stand-in for the Vintage Story master server on a loopback port. Speaks the same three
/// routes the real one does and answers with the same {status, data} envelope, so the
/// broadcaster's HTTP client is the real one and the packets on the wire are the real ones.
///
/// Every request body is kept verbatim, because what the broadcaster gets wrong when it gets
/// something wrong is the contents of the register packet: a network advertised with the wrong
/// capacity, the wrong port or an empty mod list is one players cannot join from the server list.
/// </summary>
internal sealed class FakeMasterServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<Recorded> recorded = new();
    private readonly object gate = new();

    private FakeMasterServer(WebApplication app, string url)
    {
        this.app = app;
        Url = url;
    }

    /// <summary>Base url with the trailing slash the broadcaster's client appends paths to.</summary>
    public string Url { get; }

    /// <summary>What to answer a register with. Defaults to accepting and handing out a token.</summary>
    public Func<IResult> OnRegister { get; set; } = () => Ok("session-token-1");

    /// <summary>What to answer an unregister with.</summary>
    public Func<IResult> OnUnregister { get; set; } = () => Ok("");

    public static IResult Ok(string data) => Results.Json(new { status = "ok", data });
    public static IResult Status(string status, string data = "") => Results.Json(new { status, data });

    public static async Task<FakeMasterServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        FakeMasterServer? server = null;
        app.MapPost("/api/v1/servers/{action}", async (string action, HttpRequest req) =>
        {
            string body = await new StreamReader(req.Body).ReadToEndAsync();
            server!.Record(action, body);
            return action switch
            {
                "register" => server.OnRegister(),
                "unregister" => server.OnUnregister(),
                _ => Results.NotFound(),
            };
        });

        await app.StartAsync();
        string url = app.Urls.First();
        server = new FakeMasterServer(app, url + "/api/v1/servers/");
        return server;
    }

    private void Record(string action, string body)
    {
        lock (gate) recorded.Add(new Recorded(action, JsonDocument.Parse(body).RootElement.Clone()));
    }

    /// <summary>Every call so far, in order, as (route, parsed body).</summary>
    public IReadOnlyList<Recorded> Calls { get { lock (gate) return recorded.ToArray(); } }

    public IReadOnlyList<JsonElement> Bodies(string action)
    {
        lock (gate) return recorded.Where(r => r.Action == action).Select(r => r.Body).ToArray();
    }

    /// <summary>Blocks until <paramref name="action"/> has been called, or fails the wait.</summary>
    public async Task<JsonElement> WaitForAsync(string action, int millis = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            var bodies = Bodies(action);
            if (bodies.Count > 0) return bodies[0];
            await Task.Delay(20);
        }
        throw new TimeoutException($"the broadcaster never called {action}");
    }

    /// <summary>A port nothing is listening on, for the master-server-is-down cases.</summary>
    public static string DeadUrl()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}/api/v1/servers/";
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    internal sealed record Recorded(string Action, JsonElement Body);
}

/// <summary>
/// Keeps what the broadcaster logged. Recording a call is not the same as the broadcaster having
/// read the answer to it: the request body lands here while the response is still in flight, and
/// what the broadcaster does with a register response is take the token out of it. The line it
/// writes once it has is the only signal from outside that the response has been processed, so
/// tests wait on that rather than on the request.
/// </summary>
internal sealed class RecordingLogger : ILogger<Nimbus.Registry.MasterServer.MasterServerBroadcaster>
{
    private readonly List<string> lines = new();
    private readonly object gate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (gate) lines.Add(formatter(state, exception));
    }

    public IReadOnlyList<string> Lines { get { lock (gate) return lines.ToArray(); } }

    /// <summary>Blocks until a line containing <paramref name="fragment"/> has been written.</summary>
    public async Task WaitForAsync(string fragment, int millis = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal))) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"the broadcaster never logged '{fragment}'; it logged: {string.Join(" | ", Lines)}");
    }
}
