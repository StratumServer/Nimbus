using System.Reflection;
using System.Runtime.Loader;

namespace Nimbus.Proxy;

// Scans a directory for *.dll plugins, loads each into its own AssemblyLoadContext, finds
// IPlugin implementations, and calls Initialize. One bad plugin doesn't take the proxy down:
// load and init errors are logged and the plugin is skipped.
internal sealed class PluginLoader
{
    private readonly List<LoadedPlugin> loaded = new();

    public IReadOnlyList<LoadedPlugin> Loaded => loaded;

    public void LoadAll(string pluginsDir, IProxyApi api)
    {
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
        // Isolated load context per plugin so a future Unload is feasible. Plugins still resolve
        // Nimbus.Proxy types against the default context (collectible: false) which is what we want.
        var ctx = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(dllPath), isCollectible: false);
        var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        foreach (var t in types)
        {
            if (t == null || t.IsAbstract || t.IsInterface) continue;
            if (!typeof(IPlugin).IsAssignableFrom(t)) continue;

            IPlugin instance;
            try { instance = (IPlugin)Activator.CreateInstance(t)!; }
            catch (Exception ex) { Log.Warn($"plugins: cannot instantiate {t.FullName} from {Path.GetFileName(dllPath)}: {ex.Message}"); continue; }

            try { instance.Initialize(api); }
            catch (Exception ex) { Log.Warn($"plugins: Initialize() threw for {instance.Name}: {ex.Message}"); continue; }

            loaded.Add(new LoadedPlugin(instance, Path.GetFileName(dllPath)));
            Log.Info($"plugins: loaded {instance.Name} v{instance.Version} from {Path.GetFileName(dllPath)}");
        }
    }

    public void ShutdownAll()
    {
        foreach (var lp in loaded)
        {
            try { lp.Instance.Shutdown(); }
            catch (Exception ex) { Log.Warn($"plugins: Shutdown() threw for {lp.Instance.Name}: {ex.Message}"); }
        }
    }
}

internal sealed record LoadedPlugin(IPlugin Instance, string SourceFile);
