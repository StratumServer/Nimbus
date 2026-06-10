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
