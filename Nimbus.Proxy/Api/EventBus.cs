using System.Collections.Concurrent;

namespace Nimbus.Proxy;

// Type-keyed async event bus. Handlers run sequentially in subscription order so that a
// downstream handler sees mutations from upstream handlers (Velocity-style chain).
//
// Handler exceptions are logged and swallowed,, one bad plugin shouldn't kill a session.
public sealed class EventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<ProxyEvent, Task>>> handlers = new();
    private readonly object subscribeLock = new();

    public void Subscribe<T>(Func<T, Task> handler) where T : ProxyEvent
    {
        lock (subscribeLock)
        {
            var list = handlers.GetOrAdd(typeof(T), _ => new List<Func<ProxyEvent, Task>>());
            list.Add(e => handler((T)e));
        }
    }

    public void Subscribe<T>(Action<T> handler) where T : ProxyEvent
        => Subscribe<T>(e => { handler(e); return Task.CompletedTask; });

    public async Task FireAsync<T>(T evt) where T : ProxyEvent
    {
        if (!handlers.TryGetValue(typeof(T), out var list)) return;
        // Snapshot under lock so iteration is safe against concurrent Subscribe calls.
        Func<ProxyEvent, Task>[] snapshot;
        lock (subscribeLock) { snapshot = list.ToArray(); }
        foreach (var h in snapshot)
        {
            try { await h(evt).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"event handler for {typeof(T).Name} threw: {ex.Message}"); }
        }
    }
}
