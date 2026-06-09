using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Nimbus.Proxy;

internal sealed class PluginLoader
{
    public const string CurrentApiVersion = "0.1";

    private readonly List<LoadedPlugin> loaded = new();
    private readonly HashSet<string> loadedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> disabledIds;
    private readonly PluginLoaderOptions options;

    public IReadOnlyList<LoadedPlugin> Loaded => loaded;

    public PluginLoader(PluginLoaderOptions options)
    {
        this.options = options;
        disabledIds = new HashSet<string>(options.DisabledIds, StringComparer.OrdinalIgnoreCase);
    }

    public void LoadAll(string pluginsDir, IProxyApi api)
    {
        if (!options.Enabled)
        {
            Log.Info("plugins: disabled");
            return;
        }

        if (!Directory.Exists(pluginsDir))
        {
            Log.Info($"plugins: directory '{pluginsDir}' not found, skipping plugin discovery");
            return;
        }

        var dlls = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly);
        if (dlls.Length == 0)
        {
            Log.Info($"plugins: no *.dll in '{pluginsDir}'");
            return;
        }

        foreach (var path in dlls)
        {
            try { LoadOne(path, api); }
            catch (Exception ex) { Log.Warn($"plugins: load failed for {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        Log.Info($"plugins: {loaded.Count} loaded from {pluginsDir}");
    }

    private void LoadOne(string dllPath, IProxyApi api)
    {
        var ctx = new PluginLoadContext(dllPath);
        var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        foreach (var t in types)
        {
            if (t == null || t.IsAbstract || t.IsInterface) continue;
            if (!typeof(IPlugin).IsAssignableFrom(t)) continue;

            var metadata = ReadMetadata(dllPath, t);
            if (!ValidateMetadata(metadata, dllPath)) continue;

            IPlugin instance;
            try { instance = (IPlugin)Activator.CreateInstance(t)!; }
            catch (Exception ex) { Log.Warn($"plugins: cannot instantiate {t.FullName} from {Path.GetFileName(dllPath)}: {ex.Message}"); continue; }

            if (!string.Equals(instance.Name, metadata.Name, StringComparison.Ordinal))
                Log.Warn($"plugins: {metadata.Id} manifest name '{metadata.Name}' differs from plugin name '{instance.Name}'");

            try { instance.Initialize(api); }
            catch (Exception ex) { Log.Warn($"plugins: Initialize() threw for {instance.Name}: {ex.Message}"); continue; }

            loaded.Add(new LoadedPlugin(instance, metadata, Path.GetFileName(dllPath)));
            loadedIds.Add(metadata.Id);
            Log.Info($"plugins: loaded {metadata.Id} v{metadata.Version} from {Path.GetFileName(dllPath)}");
        }
    }

    private PluginMetadata ReadMetadata(string dllPath, Type pluginType)
    {
        var manifestPath = Path.ChangeExtension(dllPath, ".plugin.json");
        if (!File.Exists(manifestPath))
        {
            var id = Path.GetFileNameWithoutExtension(dllPath);
            return new PluginMetadata(id, pluginType.Name, "0.0.0", CurrentApiVersion, Array.Empty<string>());
        }

        try
        {
            using var fs = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(fs, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new PluginManifest();

            return new PluginMetadata(
                Clean(manifest.Id, Path.GetFileNameWithoutExtension(dllPath)),
                Clean(manifest.Name, pluginType.Name),
                Clean(manifest.Version, "0.0.0"),
                Clean(manifest.ApiVersion, CurrentApiVersion),
                manifest.Dependencies.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray());
        }
        catch (Exception ex)
        {
            Log.Warn($"plugins: bad manifest for {Path.GetFileName(dllPath)}: {ex.Message}");
            var id = Path.GetFileNameWithoutExtension(dllPath);
            return new PluginMetadata(id, pluginType.Name, "0.0.0", CurrentApiVersion, Array.Empty<string>());
        }
    }

    private bool ValidateMetadata(PluginMetadata metadata, string dllPath)
    {
        if (!IsPluginId(metadata.Id))
        {
            Log.Warn($"plugins: skipping {Path.GetFileName(dllPath)} because plugin id '{metadata.Id}' is invalid");
            return false;
        }
        if (loadedIds.Contains(metadata.Id))
        {
            Log.Warn($"plugins: skipping duplicate plugin id '{metadata.Id}' from {Path.GetFileName(dllPath)}");
            return false;
        }
        if (disabledIds.Contains(metadata.Id))
        {
            Log.Info($"plugins: {metadata.Id} disabled by config");
            return false;
        }
        if (!IsApiCompatible(metadata.ApiVersion))
        {
            Log.Warn($"plugins: skipping {metadata.Id}, api {metadata.ApiVersion} is not compatible with {CurrentApiVersion}");
            return false;
        }
        foreach (var dep in metadata.Dependencies)
        {
            if (!loadedIds.Contains(dep))
            {
                Log.Warn($"plugins: skipping {metadata.Id}, missing dependency '{dep}'");
                return false;
            }
        }
        return true;
    }

    private static bool IsApiCompatible(string apiVersion)
    {
        if (!Version.TryParse(apiVersion, out var requested)) return false;
        if (!Version.TryParse(CurrentApiVersion, out var current)) return false;
        return requested.Major == current.Major && requested.Minor <= current.Minor;
    }

    private static bool IsPluginId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-') continue;
            return false;
        }
        return true;
    }

    private static string Clean(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public void ShutdownAll()
    {
        foreach (var lp in loaded)
        {
            try { lp.Instance.Shutdown(); }
            catch (Exception ex) { Log.Warn($"plugins: Shutdown() threw for {lp.Instance.Name}: {ex.Message}"); }
        }
    }
}

internal sealed record LoadedPlugin(IPlugin Instance, IPluginMetadata Metadata, string SourceFile);

internal sealed class PluginLoaderOptions
{
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<string> DisabledIds { get; set; } = Array.Empty<string>();
}

internal sealed class PluginManifest
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? ApiVersion { get; set; }
    public string[] Dependencies { get; set; } = Array.Empty<string>();
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;

    public PluginLoadContext(string pluginPath)
        : base(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
    {
        resolver = new AssemblyDependencyResolver(Path.GetFullPath(pluginPath));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var shared = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
            string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (shared != null) return shared;

        var path = resolver.ResolveAssemblyToPath(assemblyName);
        return path == null ? null : LoadFromAssemblyPath(path);
    }
}
