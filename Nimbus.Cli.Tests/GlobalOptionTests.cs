using Xunit;

namespace Nimbus.Cli.Tests;

/// <summary>
/// Where nimctl decides to connect, what it authenticates with, and what it makes of the answer.
/// The four sources have a documented order (CLI flags, then nimctl.json, then NIMCTL_* env, then
/// the built-in default) and an operator with a config file and a flag in their shell history is
/// relying on it.
///
/// The environment cases run in one class on purpose: NIMCTL_* is process-wide, and no other test
/// in this assembly reads it.
/// </summary>
public class GlobalOptionTests
{
    private static (string Host, int Port, string? Secret, List<string> Command) Parse(params string[] args)
        => Program.ParseGlobalOptions(args);

    /// <summary>Runs <paramref name="body"/> with the NIMCTL_* variables set, and puts the
    /// environment back the way it was afterwards.</summary>
    private static void WithEnvironment(string? host, string? port, string? secret, Action body)
    {
        var saved = new[] { "NIMCTL_HOST", "NIMCTL_PORT", "NIMCTL_SECRET" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("NIMCTL_HOST", host);
            Environment.SetEnvironmentVariable("NIMCTL_PORT", port);
            Environment.SetEnvironmentVariable("NIMCTL_SECRET", secret);
            body();
        }
        finally
        {
            foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
        }
    }

    [Fact]
    public void WithNothingSet_NimctlTalksToTheLocalProxyOnTheDocumentedPort()
    {
        var parsed = Parse("ping");

        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(42499, parsed.Port);
        Assert.Null(parsed.Secret);
        Assert.Equal(new[] { "ping" }, parsed.Command);
    }

    [Fact]
    public void TheGlobalFlags_AreTakenOutOfTheCommandTheyPrecede()
    {
        var parsed = Parse("--host", "10.0.0.9", "--port", "9999", "--secret", "s3cret", "ban", "--uid", "uid-1");

        Assert.Equal("10.0.0.9", parsed.Host);
        Assert.Equal(9999, parsed.Port);
        Assert.Equal("s3cret", parsed.Secret);
        // The command that survives is the one the builders see, with none of the connection
        // options left in it to be mistaken for its own arguments.
        Assert.Equal(new[] { "ban", "--uid", "uid-1" }, parsed.Command);
    }

    [Fact]
    public void TheGlobalHostAndPortFlags_AreConsumedWhereverTheyAppear()
    {
        // Including after the verb, which is where `swap` documents its own --host and --port.
        // The two spellings collide: this command line points nimctl's own connection at the
        // backend and then leaves swap with no target to send anyone to.
        var parsed = Parse("swap", "3", "--host", "10.0.0.5", "--port", "42421");

        Assert.Equal("10.0.0.5", parsed.Host);
        Assert.Equal(42421, parsed.Port);
        Assert.Equal(new[] { "swap", "3" }, parsed.Command);
    }

    [Fact]
    public void TheDocumentedSwapToAHostAndPort_CannotBeTypedToday()
    {
        // `nimctl --help` line 2 of swap advertises `swap <id> --host <h> --port <p>`, but
        // ParseGlobalOptions takes both flags before BuildPayload ever sees them. Walking the two
        // steps in the order Main does is the only way to see what an operator actually gets.
        var parsed = Parse("swap", "3", "--host", "10.0.0.5", "--port", "42421");
        var refused = Assert.Throws<ArgumentException>(() => Program.BuildPayload(parsed.Command));

        Assert.Equal("swap requires either --server <id> or both --host and --port", refused.Message);

        // Worse than the refusal is what it did on the way there: nimctl is now pointed at the
        // backend the operator meant to send the player to. Pinned rather than fixed here because
        // choosing which of the two spellings wins is a behaviour change, not a test.
        Assert.Equal("10.0.0.5", parsed.Host);
        Assert.Equal(42421, parsed.Port);
    }

    [Fact]
    public void ATrailingGlobalFlagWithNoValue_IsLeftInTheCommandRatherThanEatingTheEnd()
    {
        // Nothing follows it to be taken as the value, so it stays where it was typed and comes
        // back as an unknown command instead of silently changing where nimctl connects.
        var parsed = Parse("ping", "--host");

        Assert.Equal("127.0.0.1", parsed.Host);
        Assert.Equal(new[] { "ping", "--host" }, parsed.Command);
    }

    [Fact]
    public void TheEnvironmentIsReadWhenNoFlagSaysOtherwise()
    {
        WithEnvironment("10.1.1.1", "4242", "env-secret", () =>
        {
            var parsed = Parse("ping");

            Assert.Equal("10.1.1.1", parsed.Host);
            Assert.Equal(4242, parsed.Port);
            Assert.Equal("env-secret", parsed.Secret);
        });
    }

    [Fact]
    public void AFlagBeatsTheEnvironment()
    {
        WithEnvironment("10.1.1.1", "4242", "env-secret", () =>
        {
            var parsed = Parse("--host", "10.2.2.2", "--secret", "flag-secret", "ping");

            Assert.Equal("10.2.2.2", parsed.Host);
            Assert.Equal("flag-secret", parsed.Secret);
            // The port had no flag, so the environment still stands for it.
            Assert.Equal(4242, parsed.Port);
        });
    }

    [Fact]
    public void ANonNumericPortInTheEnvironment_FallsBackToTheDefault()
    {
        WithEnvironment(null, "not-a-port", null, () =>
        {
            Assert.Equal(42499, Parse("ping").Port);
        });
    }

    // ---- nimctl.json ----

    [Fact]
    public void AConfigFile_SuppliesTheHostPortAndSecret()
    {
        using var dir = new TempDir("""{"Host":"10.3.3.3","Port":4243,"Secret":"file-secret"}""");

        var applied = Program.ApplyConfigFile(new[] { dir.Path }, "127.0.0.1", 42499, null);

        Assert.Equal("10.3.3.3", applied.Host);
        Assert.Equal(4243, applied.Port);
        Assert.Equal("file-secret", applied.Secret);
    }

    [Fact]
    public void AConfigFile_LeavesAloneWhateverItDoesNotMention()
    {
        using var dir = new TempDir("""{"Port":4243}""");

        var applied = Program.ApplyConfigFile(new[] { dir.Path }, "10.9.9.9", 42499, "env-secret");

        Assert.Equal("10.9.9.9", applied.Host);
        Assert.Equal(4243, applied.Port);
        Assert.Equal("env-secret", applied.Secret);
    }

    [Fact]
    public void AFieldOfTheWrongType_IsIgnoredRatherThanCrashingTheCli()
    {
        using var dir = new TempDir("""{"Port":"4243","Host":42}""");

        var applied = Program.ApplyConfigFile(new[] { dir.Path }, "127.0.0.1", 42499, null);

        Assert.Equal("127.0.0.1", applied.Host);
        Assert.Equal(42499, applied.Port);
    }

    [Fact]
    public void AMalformedConfigFile_IsIgnoredRatherThanStoppingTheCommand()
    {
        // An operator halfway through editing nimctl.json still gets to run nimctl.
        using var dir = new TempDir("{ this is not json");

        var applied = Program.ApplyConfigFile(new[] { dir.Path }, "127.0.0.1", 42499, null);

        Assert.Equal("127.0.0.1", applied.Host);
        Assert.Equal(42499, applied.Port);
    }

    [Fact]
    public void OnlyTheFirstConfigFileFoundIsRead()
    {
        using var first = new TempDir("""{"Host":"10.4.4.4"}""");
        using var second = new TempDir("""{"Host":"10.5.5.5","Port":4244}""");

        var applied = Program.ApplyConfigFile(new[] { first.Path, second.Path }, "127.0.0.1", 42499, null);

        Assert.Equal("10.4.4.4", applied.Host);
        // The second file is not consulted at all, not even for the fields the first one left out.
        Assert.Equal(42499, applied.Port);
    }

    [Fact]
    public void ADirectoryWithNoConfigFile_IsSteppedOverToTheNextOne()
    {
        using var empty = new TempDir(null);
        using var real = new TempDir("""{"Host":"10.6.6.6"}""");

        var applied = Program.ApplyConfigFile(new[] { empty.Path, real.Path }, "127.0.0.1", 42499, null);

        Assert.Equal("10.6.6.6", applied.Host);
    }

    // ---- help and exit codes ----

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("help")]
    public void EverySpellingOfHelp_IsRecognised(string arg) => Assert.True(Program.IsHelp(arg));

    [Theory]
    [InlineData("ping")]
    [InlineData("-help")]
    [InlineData("--h")]
    public void SomethingThatIsNotHelp_IsNotTreatedAsIt(string arg) => Assert.False(Program.IsHelp(arg));

    [Fact]
    public void AProxyThatAnsweredNotOk_ExitsThree()
    {
        // The exit code is what a shell script wrapping nimctl branches on, so a refused ban has
        // to be distinguishable from a granted one.
        Assert.Equal(3, Program.ExitCodeFromResponse("""{"ok":false,"reason":"permission denied"}"""));
        Assert.Equal(0, Program.ExitCodeFromResponse("""{"ok":true}"""));
    }

    [Fact]
    public void AnAnswerWithNoVerdictInIt_ExitsZero()
    {
        // `list` answers with sessions and no ok field, and that is not a failure.
        Assert.Equal(0, Program.ExitCodeFromResponse("""{"sessions":[]}"""));
        Assert.Equal(0, Program.ExitCodeFromResponse("not json at all"));
        Assert.Equal(0, Program.ExitCodeFromResponse(""));
    }

    [Fact]
    public void TheAnswerIsPrintedIndentedWhenItIsJson()
    {
        var printed = Program.PrettyPrint("""{"ok":true,"count":2}""");

        Assert.Contains("\"ok\": true", printed);
        Assert.Contains('\n', printed);
    }

    [Fact]
    public void AnAnswerThatIsNotJson_IsShownAsItCameRatherThanSwallowed()
    {
        Assert.Equal("something went sideways", Program.PrettyPrint("something went sideways"));
        Assert.Equal("(no response)", Program.PrettyPrint("   "));
    }

    /// <summary>A throwaway directory, optionally holding a nimctl.json with the given text.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir(string? configJson)
        {
            Path = Directory.CreateTempSubdirectory("nimctl-test").FullName;
            if (configJson != null)
                File.WriteAllText(System.IO.Path.Combine(Path, "nimctl.json"), configJson);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* left behind in temp */ }
        }
    }
}
