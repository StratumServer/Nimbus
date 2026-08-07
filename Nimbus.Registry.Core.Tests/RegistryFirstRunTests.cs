using Nimbus.Shared;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// What a standalone registry writes into a directory that has no nimbus.registry.toml in it,
/// which is what an operator unzips out of the release. Same shape as the proxy's first run
/// (#94): the secret is generated on the machine that needs it rather than shipped as a literal
/// anybody can read out of this repository, and the file says where its twins go.
///
/// Nimbus.Registry.Program is a Web SDK executable with no test project of its own, so the write
/// lives in this library and Program keeps the console lines around it.
/// </summary>
public class RegistryFirstRunTests
{
    /// <summary>A directory with no config in it, and the path one would land at.</summary>
    private static string FreshInstallDir(out string configPath)
    {
        string dir = Path.Combine(Path.GetTempPath(), "nimbus-registry-firstrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "nimbus.registry.toml");
        return dir;
    }

    /// <summary>The write half of Program's opening sequence, then the same loader reading it
    /// back off disk. Going through the file rather than through `new RegistryConfig()` is the
    /// point: those are the bytes the operator ends up running.</summary>
    private static RegistryConfig FirstRun(string configPath)
    {
        Assert.True(RegistryFirstRun.TryWriteConfig(configPath, out string? error), error);
        return TomlConfig.LoadOrCreate<RegistryConfig>(configPath);
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* a leftover temp dir is harmless */ }
    }

    [Fact]
    public void AFreshInstall_GeneratesItsOwnSecretInsteadOfShippingOne()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            var cfg = FirstRun(path);

            Assert.True(File.Exists(path), "the first run left no config file for the operator to edit");
            Assert.False(SecretPlaceholders.IsPlaceholder(cfg.SharedSecret),
                "the written secret is one of the values published in this repository");
            Assert.Equal(SecretGenerator.Length, cfg.SharedSecret.Length);
            Assert.All(cfg.SharedSecret, c => Assert.True(char.IsAsciiLetterOrDigit(c),
                $"'{c}' would need quoting or escaping somewhere on its way to a backend"));
            // The operator retypes this into every backend, sometimes off a panel screen, so a
            // character pair that is only distinguishable in some fonts costs them an HMAC
            // failure with nothing to go on.
            Assert.All(cfg.SharedSecret, c => Assert.DoesNotContain(c, "0Oo1lI"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AFreshInstall_SaysWhereTheOtherEndsOfTheSecretGo()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            FirstRun(path);
            string written = File.ReadAllText(path);

            // A generated secret is no use to anyone without the two places it has to be copied
            // to, and the file is where the operator is looking.
            Assert.Contains("nimbus-server.json", written, StringComparison.Ordinal);
            Assert.Contains("registry.shared_secret", written, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AFreshInstall_StillWritesAConfigItsOwnLoaderReadsBack()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            var cfg = FirstRun(path);

            // The comment lines are inserted into the file after Tomlyn has serialized it, so
            // this is the assertion that a mangled insert cannot pass.
            Assert.Equal("http://127.0.0.1:8765", cfg.BindUrl);
            Assert.Empty(RegistryConfigWarnings.ApiTokens(cfg));
            Assert.Contains(cfg.SharedSecret, cfg.AllSecrets());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TwoFreshInstalls_DoNotEndUpSharingASecret()
    {
        string first = FreshInstallDir(out string firstPath);
        string second = FreshInstallDir(out string secondPath);
        try
        {
            // Two registries on one secret are one registry as far as authentication goes:
            // either network's backends can heartbeat into the other.
            Assert.NotEqual(FirstRun(firstPath).SharedSecret, FirstRun(secondPath).SharedSecret);
        }
        finally { Cleanup(first); Cleanup(second); }
    }

    [Fact]
    public void AnExistingConfig_IsLeftAloneRatherThanRegenerated()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            var written = FirstRun(path);

            // Program only writes when the file is missing. Regenerating on every boot would cut
            // every backend off from the registry after a restart.
            Assert.Equal(written.SharedSecret, TomlConfig.LoadOrCreate<RegistryConfig>(path).SharedSecret);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AFirstRunThatCannotWriteItsConfig_GivesUpOnTheSecretRatherThanTheBoot()
    {
        // A read-only install directory, or one the service account cannot reach. Program falls
        // through to LoadOrCreate, which writes the plain defaults; the placeholder they carry is
        // the one the boot warning is about, so nothing is quietly exposed.
        string unreachable = Path.Combine(
            Path.GetTempPath(), "nimbus-no-such-dir-" + Guid.NewGuid().ToString("N"), "nimbus.registry.toml");

        Assert.False(RegistryFirstRun.TryWriteConfig(unreachable, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error), "the caller has nothing to print");
        Assert.False(File.Exists(unreachable));
    }

    [Fact]
    public void AnEmptyDirectory_IsAFirstRunAndADirectoryWithAConfigIsNot()
    {
        string dir = FreshInstallDir(out string path);
        try
        {
            Assert.True(RegistryFirstRun.IsFirstRun(path));

            FirstRun(path);

            Assert.False(RegistryFirstRun.IsFirstRun(path));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ALegacyJsonSibling_IsAMigrationRatherThanAFirstRun()
    {
        // A registry started next to a nimbus.registry.json is an existing network: its backends
        // already hold a secret, and TomlConfig carries that value into the new file. Generating
        // over it would lock out every backend at once, under a line written for first installs.
        string dir = FreshInstallDir(out string path);
        try
        {
            File.WriteAllText(Path.ChangeExtension(path, ".json"),
                "{\"SharedSecret\":\"the-secret-this-network-runs-on\"}");

            Assert.False(RegistryFirstRun.IsFirstRun(path));

            // And the value that survives is the network's own, not a fresh one.
            Assert.Equal("the-secret-this-network-runs-on",
                TomlConfig.LoadOrCreate<RegistryConfig>(path).SharedSecret);
        }
        finally { Cleanup(dir); }
    }
}
