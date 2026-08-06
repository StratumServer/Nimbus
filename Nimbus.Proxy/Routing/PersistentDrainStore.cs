using System.Text.Json;

namespace Nimbus.Proxy;

// Remembers which backends an operator drained so a proxy restart does not put them back into
// rotation. The file is written on every drain/undrain and read once, when BackendRouter is built.
internal sealed class PersistentDrainStore
{
    // Releases up to v0.4.0 serialized with the default naming policy, so every file on disk
    // today carries "Drained"/"UpdatedAtUnix" while Load only ever looked for "drained": the
    // flags were written faithfully and never read back. The writer now emits camelCase, which
    // the readers on both sides of that fix accept, and the reader below takes either spelling
    // so files from any version still load.
    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string path;
    private readonly object gate = new();

    public PersistentDrainStore(string path)
    {
        this.path = path;
    }

    public IReadOnlyList<string> Load()
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!TryGetDrained(doc.RootElement, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var result = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var serverId = item.GetString();
                if (!string.IsNullOrWhiteSpace(serverId)) result.Add(serverId);
            }
            return result;
        }
        catch (Exception ex)
        {
            // A drain file we cannot read must not stop the proxy from booting: warn and route
            // as if nothing was drained, which is the same outcome as no file at all.
            Log.Warn($"drain store load failed from '{path}': {ex.Message}");
            return Array.Empty<string>();
        }
    }

    // TryGetProperty is case-sensitive, so the two spellings have to be asked for by name.
    // camelCase first because that is what the current writer produces.
    private static bool TryGetDrained(JsonElement root, out JsonElement arr)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            arr = default;
            return false;
        }
        return root.TryGetProperty("drained", out arr) || root.TryGetProperty("Drained", out arr);
    }

    public void Save(IEnumerable<string> drained)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var payload = new DrainState
                {
                    Drained = drained
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    UpdatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };

                // Write beside the target and move into place so a reader never sees a half file.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, SaveOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"drain store save failed to '{path}': {ex.Message}");
            }
        }
    }

    private sealed class DrainState
    {
        public string[] Drained { get; set; } = Array.Empty<string>();
        public long UpdatedAtUnix { get; set; }
    }
}
