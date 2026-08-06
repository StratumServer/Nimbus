using System.Text.Json;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The drain file is the only thing standing between an operator draining a broken backend and
/// a proxy restart putting players back on it, so both halves of the store are pinned here: what
/// the writer produces, and every shape the reader can be handed at boot.
/// </summary>
public class PersistentDrainStoreTests
{
    [Fact]
    public void SavedFlags_LoadBackThroughARealFile()
    {
        using var dir = new TempDir();
        var store = new PersistentDrainStore(dir.File);

        store.Save(new[] { "creative", "hub" });

        // A fresh instance, as a restarted proxy would build one: nothing carries over in memory.
        Assert.Equal(new[] { "creative", "hub" }, new PersistentDrainStore(dir.File).Load());
    }

    [Fact]
    public void FileWrittenByThePreviousWriter_StillLoads()
    {
        // Every drain file on disk today was written with the default naming policy, so it says
        // "Drained". The reader has to keep taking that spelling or those operators lose their
        // flags on the upgrade that fixes this. Literal on purpose: no writer produces it now.
        using var dir = new TempDir();
        File.WriteAllText(dir.File, """
        {
          "Drained": [
            "creative",
            "hub"
          ],
          "UpdatedAtUnix": 1754467200
        }
        """);

        Assert.Equal(new[] { "creative", "hub" }, new PersistentDrainStore(dir.File).Load());
    }

    [Fact]
    public void TheWriterEmitsCamelCase_WhichTheOlderReaderAlsoAccepts()
    {
        // v0.4.0 and earlier read "drained" only. Writing camelCase means a file this proxy
        // wrote survives a rollback to those builds, on top of the reader fix above.
        using var dir = new TempDir();
        new PersistentDrainStore(dir.File).Save(new[] { "creative" });

        using var doc = JsonDocument.Parse(File.ReadAllText(dir.File));
        Assert.True(doc.RootElement.TryGetProperty("drained", out var arr));
        Assert.Equal("creative", Assert.Single(arr.EnumerateArray()).GetString());
        Assert.True(doc.RootElement.TryGetProperty("updatedAtUnix", out var stamp));
        Assert.True(stamp.GetInt64() > 0);
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        using var dir = new TempDir();

        // The first boot after enabling persistence has no file yet, which is not an error.
        Assert.Empty(new PersistentDrainStore(dir.File).Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_LoadsEmptyAndSavesNothing(string path)
    {
        // persist_drain_flags off leaves the path blank; the store must degrade to a no-op
        // rather than throw at construction or write to the working directory.
        var store = new PersistentDrainStore(path);
        store.Save(new[] { "creative" });

        Assert.Empty(store.Load());
    }

    [Fact]
    public void CorruptFile_LoadsEmptyWithoutThrowing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File, "{ this is not json");

        // Truncated by a full disk or hand-edited into garbage: the proxy still has to boot,
        // so the flags are dropped with a warning rather than taking the process down.
        Assert.Empty(new PersistentDrainStore(dir.File).Load());
    }

    [Theory]
    [InlineData("{}")]                                    // no drained key at all
    [InlineData("""{"drained":"creative"}""")]            // present but not an array
    [InlineData("""{"drained":null}""")]
    [InlineData("[\"creative\"]")]                        // root is an array, not an object
    [InlineData("\"creative\"")]                          // root is a bare string
    public void ValidJsonWithoutADrainedArray_LoadsEmpty(string json)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File, json);

        Assert.Empty(new PersistentDrainStore(dir.File).Load());
    }

    [Fact]
    public void NonStringAndBlankEntries_AreSkipped()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File, """{"drained":["creative",42,null,"","   ","hub"]}""");

        // A hand-edited list must not inject empty ids the router would then match nothing on.
        Assert.Equal(new[] { "creative", "hub" }, new PersistentDrainStore(dir.File).Load());
    }

    [Fact]
    public void SaveNormalisesTheList()
    {
        using var dir = new TempDir();
        var store = new PersistentDrainStore(dir.File);

        store.Save(new[] { "hub", "CREATIVE", "creative", "  ", "", "alpha" });

        // Ids are matched case-insensitively by the router, so the file keeps one spelling per
        // backend and sorts them, which also keeps the file stable across saves.
        Assert.Equal(new[] { "alpha", "CREATIVE", "hub" }, store.Load());
    }

    [Fact]
    public void SaveOverwritesTheEarlierList()
    {
        using var dir = new TempDir();
        var store = new PersistentDrainStore(dir.File);

        store.Save(new[] { "creative", "hub" });
        store.Save(new[] { "hub" });

        // Undraining writes the remaining set, so the dropped id must not survive in the file.
        Assert.Equal(new[] { "hub" }, new PersistentDrainStore(dir.File).Load());
        Assert.False(File.Exists(dir.File + ".tmp"), "the scratch file must not be left behind");
    }

    [Fact]
    public void SaveEmpty_LeavesNothingDrained()
    {
        using var dir = new TempDir();
        var store = new PersistentDrainStore(dir.File);

        store.Save(new[] { "creative" });
        store.Save(Array.Empty<string>());

        Assert.Empty(new PersistentDrainStore(dir.File).Load());
    }

    [Fact]
    public void Save_CreatesTheMissingStateDirectory()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "state", "drain.json");

        // The configured state directory does not exist on a first run.
        new PersistentDrainStore(nested).Save(new[] { "creative" });

        Assert.Equal(new[] { "creative" }, new PersistentDrainStore(nested).Load());
    }

    [Fact]
    public void UnwritablePath_IsWarnedAboutRatherThanThrown()
    {
        using var dir = new TempDir();
        var occupied = Path.Combine(dir.Path, "occupied");
        Directory.CreateDirectory(occupied);

        // The path is an existing directory, so the write cannot land. A drain command must
        // still succeed in memory rather than fault the admin socket on a bad state path.
        var store = new PersistentDrainStore(occupied);
        store.Save(new[] { "creative" });

        Assert.Empty(store.Load());
    }

    /// <summary>A throwaway directory holding one drain file, deleted with the test.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = Directory.CreateTempSubdirectory("nimbus-drain-test").FullName;
            File = System.IO.Path.Combine(Path, "drain.json");
        }

        public string Path { get; }

        public string File { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* left behind in temp */ }
        }
    }
}
