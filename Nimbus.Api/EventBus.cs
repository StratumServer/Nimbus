using System.Collections.Concurrent;

namespace Nimbus.Proxy;

public sealed class EventBus
{
    private readonly ConcurrentDictionary<Type, List<Func<ProxyEvent, Task>>> handlers = new();
    private readonly object subscribeLock = new();

    public Action<string>? WarningSink { get; set; }

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

    // Lets a hot path skip the work of building an event nobody listens to. Reads Count under
    // the same lock Subscribe mutates the list with: List<T> is not safe to read while another
    // thread is adding to it, and a plugin hot-reload subscribes while sessions are live.
    public bool HasSubscribers<T>() where T : ProxyEvent
    {
        if (!handlers.TryGetValue(typeof(T), out var list)) return false;
        lock (subscribeLock) return list.Count > 0;
    }

    public void ClearSubscriptions()
    {
        lock (subscribeLock)
        {
            handlers.Clear();
        }
    }

    public async Task FireAsync<T>(T evt) where T : ProxyEvent
    {
        if (!handlers.TryGetValue(typeof(T), out var list)) return;

        Func<ProxyEvent, Task>[] snapshot;
        lock (subscribeLock) { snapshot = list.ToArray(); }
        foreach (var h in snapshot)
        {
            try { await h(evt).ConfigureAwait(false); }
            catch (Exception ex) { WarningSink?.Invoke($"event handler for {typeof(T).Name} threw: {ex.Message}"); }
        }
    }
}
