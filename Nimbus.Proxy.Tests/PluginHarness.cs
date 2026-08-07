using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// A scratch plugin directory holding real, separately built plugin assemblies. The loader gets
/// no stand-ins: it opens these dlls through its own collectible load context and resolves the
/// plugin types by reflection, which is the only way the manifest handling, the api-version gate
/// and the failure paths can be shown to work.
///
/// Each instance owns a fresh temp directory so a test that rewrites a manifest cannot reach the
/// next one.
/// </summary>
internal sealed class PluginDir : IDisposable
{
    /// <summary>The sample plugin shipped in the repo, built in-tree.</summary>
    public const string Sample = "Nimbus.SamplePlugin.dll";

    /// <summary>Fixture plugin that throws out of Initialize.</summary>
    public const string BrokenInit = "Nimbus.BrokenPlugin.dll";

    /// <summary>Fixture plugin that loads fine and throws out of Shutdown.</summary>
    public const string BrokenShutdown = "Nimbus.ShutdownPlugin.dll";

    private static readonly string Staged =
        System.IO.Path.Combine(AppContext.BaseDirectory, "plugin-fixtures");

    private PluginDir(string path) => Path = path;

    public string Path { get; }

    public static PluginDir Empty()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "nimbus-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new PluginDir(path);
    }

    /// <summary>A directory holding the named fixture dlls, each with the manifest it ships with.</summary>
    public static PluginDir With(params string[] dlls)
    {
        var dir = Empty();
        foreach (var dll in dlls) dir.Add(dll);
        return dir;
    }

    /// <summary>Copies a fixture in, optionally under a different file name so the same plugin can
    /// appear twice.</summary>
    public PluginDir Add(string dll, string? asName = null)
    {
        string target = System.IO.Path.Combine(Path, asName ?? dll);
        File.Copy(System.IO.Path.Combine(Staged, dll), target, overwrite: true);

        string manifest = System.IO.Path.Combine(Staged, System.IO.Path.ChangeExtension(dll, ".plugin.json"));
        if (File.Exists(manifest))
            File.Copy(manifest, System.IO.Path.ChangeExtension(target, ".plugin.json"), overwrite: true);
        return this;
    }

    /// <summary>Writes a manifest next to <paramref name="dll"/> verbatim, for the malformed cases.</summary>
    public PluginDir WriteManifest(string dll, string json)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, System.IO.Path.ChangeExtension(dll, ".plugin.json")), json);
        return this;
    }

    /// <summary>Rewrites one field of an existing manifest, leaving the rest as shipped.</summary>
    public PluginDir PatchManifest(string dll, string key, JsonNode? value)
    {
        string path = System.IO.Path.Combine(Path, System.IO.Path.ChangeExtension(dll, ".plugin.json"));
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node[key] = value;
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return this;
    }

    /// <summary>Drops one field out of an existing manifest, for the "the author never wrote this
    /// key" cases.</summary>
    public PluginDir RemoveManifestField(string dll, string key)
    {
        string path = System.IO.Path.Combine(Path, System.IO.Path.ChangeExtension(dll, ".plugin.json"));
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove(key);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return this;
    }

    public PluginDir DeleteManifest(string dll)
    {
        File.Delete(System.IO.Path.Combine(Path, System.IO.Path.ChangeExtension(dll, ".plugin.json")));
        return this;
    }

    /// <summary>Drops a file the loader will try to open as an assembly and cannot.</summary>
    public PluginDir AddGarbageDll(string name)
    {
        File.WriteAllText(System.IO.Path.Combine(Path, name), "this is not a PE image");
        return this;
    }

    public PluginDir Remove(string dll)
    {
        File.Delete(System.IO.Path.Combine(Path, dll));
        string manifest = System.IO.Path.Combine(Path, System.IO.Path.ChangeExtension(dll, ".plugin.json"));
        if (File.Exists(manifest)) File.Delete(manifest);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* the load context may still hold a handle on Windows; a temp dir left behind is harmless */ }
    }
}

/// <summary>
/// The proxy surface a plugin is handed. Backed by a plain server table rather than a live
/// ProxyListener, because what the plugin tests need to see is which calls a plugin made and
/// what it did with the answers.
/// </summary>
internal sealed class FakeProxyApi : IProxyApi
{
    private readonly Dictionary<string, IServerInfo> servers = new(StringComparer.OrdinalIgnoreCase);

    public EventBus Events { get; } = new();

    public IEnumerable<IPlayer> Players => Array.Empty<IPlayer>();

    /// <summary>Every server id the plugin asked about, in order.</summary>
    public List<string> Resolved { get; } = new();

    /// <summary>Lines the plugin logged, prefixed with the level it chose.</summary>
    public List<string> Logged { get; } = new();

    public FakeProxyApi WithServer(string serverId, string host, int port)
    {
        servers[serverId] = new ServerInfo { ServerId = serverId, Host = host, Port = port };
        return this;
    }

    public bool TryGetPlayer(long sessionId, out IPlayer player) { player = null!; return false; }
    public IPlayer? FindPlayerByUid(string uid) => null;
    public IPlayer? FindPlayerByName(string name) => null;

    public Task<IServerInfo?> ResolveServerAsync(string serverId, CancellationToken ct)
    {
        Resolved.Add(serverId);
        return Task.FromResult(servers.TryGetValue(serverId, out var s) ? s : null);
    }

    public void LogInfo(string pluginName, string message) => Logged.Add($"info {pluginName}: {message}");
    public void LogWarn(string pluginName, string message) => Logged.Add($"warn {pluginName}: {message}");
}

/// <summary>A player for the events plugins are given. Nothing here is proxied anywhere.</summary>
internal sealed class FakePlayer : IPlayer
{
    public FakePlayer(string? uid = "uid-1", string? name = "alice")
    {
        Uid = uid;
        Name = name;
    }

    public long Id => 1;
    public string? Uid { get; }
    public string? Name { get; }
    public string ClientRemote => "203.0.113.7";
    public IServerInfo? CurrentServer { get; set; }
    public bool SupportsSeamlessTransfers => false;

    // Nothing in the plugin tests moves a player through the api; the events they carry a player
    // on are the subject, so these are here to satisfy IPlayer and nothing more.
    public Task<string?> TransferAsync(IServerInfo target, string? reason = null)
        => Task.FromResult<string?>(null);

    public Task<string?> TransferAsync(IServerInfo target, string mode, string? reason = null)
        => Task.FromResult<string?>(null);

    public void Disconnect(string? reason = null) { }
}
