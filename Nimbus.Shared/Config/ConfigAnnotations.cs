using System.Text;

namespace Nimbus.Shared;

// TomlConfig writes a config by serialising a POCO, so the only way a comment reaches the file is
// to put it there afterwards. This is the shared way to do it.
public static class ConfigAnnotations
{
    // Inserts the note immediately above the first line beginning with <paramref name="key"/>,
    // rewriting the file as UTF-8 without a BOM. Best effort by design: a missing key leaves the
    // file exactly as written, since a valid config without a comment beats a mangled one.
    //
    // The proxy and the standalone registry both mint a shared secret on first run and want a note
    // over it, differing in the key they wrote and in which twins they point the operator at, so
    // each passes its own key and note here.
    public static void InsertAbove(string path, string key, params string[] note)
    {
        var lines = File.ReadAllLines(path).ToList();
        int at = lines.FindIndex(l => l.StartsWith(key, StringComparison.Ordinal));
        if (at < 0) return;
        lines.InsertRange(at, note);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}
