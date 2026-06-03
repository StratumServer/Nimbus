using System.Text.Json;

namespace Nimbus.Proxy;

internal sealed class PersistentDrainStore
{
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
            if (!doc.RootElement.TryGetProperty("drained", out var arr) || arr.ValueKind != JsonValueKind.Array)
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
            Log.Warn($"drain store load failed from '{path}': {ex.Message}");
            return Array.Empty<string>();
        }
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

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
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
