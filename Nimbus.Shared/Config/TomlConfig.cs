using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace Nimbus.Shared;

// Shared TOML config loader. Snake_case keys, auto-migration from a legacy JSON sibling
// (same path, .json extension) on first load.
public static class TomlConfig
{
    private static readonly TomlModelOptions Options = new()
    {
        ConvertPropertyName = ToSnakeCase,
        ConvertFieldName = ToSnakeCase,
    };

    // Load `T` from `tomlPath`. If the file does not exist:
    //   - if a sibling `.json` (same stem) exists, load it as JSON and write a TOML next to it,
    //     keeping the original as `<name>.json.migrated`.
    //   - otherwise write a default `T` to `tomlPath`.
    // Returns the loaded (or freshly-written) instance.
    public static T LoadOrCreate<T>(string tomlPath, Func<T, T>? postLoad = null) where T : class, new()
    {
        if (!tomlPath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must end with .toml", nameof(tomlPath));

        T value;
        if (File.Exists(tomlPath))
        {
            value = LoadFile<T>(tomlPath);
        }
        else
        {
            var jsonSibling = Path.ChangeExtension(tomlPath, ".json");
            if (File.Exists(jsonSibling))
            {
                value = JsonSerializer.Deserialize<T>(File.ReadAllText(jsonSibling),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new T();
                Save(tomlPath, value);
                try { File.Move(jsonSibling, jsonSibling + ".migrated", overwrite: true); } catch { }
            }
            else
            {
                value = new T();
                Save(tomlPath, value);
            }
        }
        return postLoad != null ? postLoad(value) : value;
    }

    public static T LoadFile<T>(string tomlPath) where T : class, new()
    {
        var text = File.ReadAllText(tomlPath);
        var result = Toml.ToModel<T>(text, sourcePath: tomlPath, options: Options);
        return result ?? new T();
    }

    public static void Save<T>(string tomlPath, T value) where T : class
    {
        var text = Toml.FromModel(value, Options);
        File.WriteAllText(tomlPath, text, new UTF8Encoding(false));
    }

    // PascalCase -> snake_case. Handles consecutive uppercase by splitting before the last
    // upper followed by a lower (HTTPServer -> http_server, ServerId -> server_id).
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool prevLower = i > 0 && char.IsLower(name[i - 1]);
                bool nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && (prevLower || nextLower)) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
