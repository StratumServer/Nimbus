using Xunit;

namespace Nimbus.Cli.Tests;

/// <summary>
/// What a command line becomes on the admin socket. These are string-for-string comparisons on
/// purpose: the admin endpoint reads named fields off a JSON object, so a renamed key or a port
/// sent as a string is a command that silently does nothing, and nothing between nimctl and the
/// proxy would catch it.
/// </summary>
public class PayloadBuilderTests
{
    private static string Payload(params string[] args)
        => Program.Serialize(Program.BuildPayload(args.ToList()));

    private static ArgumentException Refused(params string[] args)
        => Assert.Throws<ArgumentException>(() => Program.BuildPayload(args.ToList()));

    [Theory]
    [InlineData("ping")]
    [InlineData("help")]
    [InlineData("list")]
    [InlineData("plugins")]
    [InlineData("sticky")]
    [InlineData("route")]
    [InlineData("bans")]
    [InlineData("reload")]
    public void AVerbThatTakesNoArguments_SendsItsNameAndNothingElse(string verb)
    {
        Assert.Equal($$"""{"cmd":"{{verb}}"}""", Payload(verb));
    }

    [Fact]
    public void StatusAndKick_CarryTheSessionIdAsANumber()
    {
        Assert.Equal("""{"cmd":"status","id":7}""", Payload("status", "7"));
        Assert.Equal("""{"cmd":"kick","id":7}""", Payload("kick", "7"));
    }

    [Fact]
    public void ASessionIdThatIsNotANumber_IsRefusedBeforeAnythingIsSent()
    {
        Assert.Equal("invalid <id>: alice", Refused("kick", "alice").Message);
        Assert.Equal("missing <id>", Refused("kick").Message);
    }

    [Fact]
    public void Servers_AlwaysStatesWhetherItWantsARefresh()
    {
        // The flag is sent either way rather than omitted, so the proxy never has to guess what
        // an absent field meant.
        Assert.Equal("""{"cmd":"servers","refresh":false}""", Payload("servers"));
        Assert.Equal("""{"cmd":"servers","refresh":true}""", Payload("servers", "--refresh"));
    }

    // ---- swap ----

    [Fact]
    public void ASwapToANamedBackend_SendsTheServerIdAndNothingItWasNotGiven()
    {
        Assert.Equal("""{"cmd":"swap","id":3,"serverId":"creative"}""",
            Payload("swap", "3", "--server", "creative"));
        // --serverId is accepted for operators who read the admin protocol rather than the help.
        Assert.Equal("""{"cmd":"swap","id":3,"serverId":"creative"}""",
            Payload("swap", "3", "--serverId", "creative"));
    }

    [Fact]
    public void ASwapToAHostAndPort_SendsThePortAsANumber()
    {
        // A quoted port would land in the admin frame as a string and the swap would be refused
        // for having no port at all. These two flags reach this builder off a real command line
        // since #81: see TheGlobalFlagsStopAtTheVerb_LeavingWhatFollowsToTheCommand.
        Assert.Equal("""{"cmd":"swap","id":3,"host":"10.0.0.5","port":42421}""",
            Payload("swap", "3", "--host", "10.0.0.5", "--port", "42421"));
    }

    [Fact]
    public void ASwapToAPortThatIsNotANumber_IsRefusedInWordsRatherThanThrownOn()
    {
        // Same message the connection-side --port gives, since an operator typing this one has no
        // way of knowing which of the two parsers they landed in.
        Assert.Equal("--port takes a port number",
            Refused("swap", "3", "--host", "10.0.0.5", "--port", "42421a").Message);
    }

    [Fact]
    public void ASwapCarriesTheReasonAndTheModeItWasAskedFor()
    {
        Assert.Equal("""{"cmd":"swap","id":3,"serverId":"creative","reason":"event start","mode":"seamless"}""",
            Payload("swap", "3", "--server", "creative", "--reason", "event start", "--seamless"));
        Assert.Equal("""{"cmd":"swap","id":3,"serverId":"creative","mode":"redirect"}""",
            Payload("swap", "3", "--server", "creative", "--redirect"));
        // --splice is the deprecated spelling of --seamless and has to keep meaning the same thing.
        Assert.Equal("""{"cmd":"swap","id":3,"serverId":"creative","mode":"seamless"}""",
            Payload("swap", "3", "--server", "creative", "--splice"));
    }

    [Fact]
    public void ASwapWithNoModeAtAll_LeavesTheChoiceToTheProxy()
    {
        // No "mode" key, so the proxy's transfers.default_mode decides. Sending one here would
        // quietly override the operator's config.
        Assert.DoesNotContain("mode", Payload("swap", "3", "--server", "creative"));
    }

    [Fact]
    public void ASwapAskingForBothModesAtOnce_IsRefused()
    {
        Assert.Equal("--seamless and --redirect are mutually exclusive",
            Refused("swap", "3", "--server", "creative", "--seamless", "--redirect").Message);
    }

    [Fact]
    public void ASwapWithNoTarget_IsRefusedRatherThanSentWithoutOne()
    {
        const string expected = "swap requires either --server <id> or both --host and --port";
        Assert.Equal(expected, Refused("swap", "3").Message);
        // A host without a port is half a target, which is not a target.
        Assert.Equal(expected, Refused("swap", "3", "--host", "10.0.0.5").Message);
        Assert.Equal(expected, Refused("swap", "3", "--port", "42421").Message);
    }

    // ---- ban ----

    [Fact]
    public void ABanByUid_SendsTheUidAlone()
    {
        Assert.Equal("""{"cmd":"ban","uid":"uid-1"}""", Payload("ban", "--uid", "uid-1"));
    }

    [Fact]
    public void ABanByNameWithEveryOption_SendsAllOfThemUnderTheAdminNames()
    {
        Assert.Equal(
            """{"cmd":"ban","player":"griefer","serverId":"creative","reason":"griefing","duration":3600}""",
            Payload("ban", "--player", "griefer", "--server", "creative",
                    "--reason", "griefing", "--duration", "3600"));
        // --name reads the same as --player to anyone typing it.
        Assert.Equal("""{"cmd":"ban","player":"griefer"}""", Payload("ban", "--name", "griefer"));
    }

    [Fact]
    public void ABanWithADurationOfZero_SendsTheZeroThatMeansPermanent()
    {
        // Not the same as omitting it as far as reading the command back goes, and the admin
        // side treats both as permanent, so the explicit zero has to survive the trip.
        Assert.Equal("""{"cmd":"ban","uid":"uid-1","duration":0}""",
            Payload("ban", "--uid", "uid-1", "--duration", "0"));
    }

    [Fact]
    public void ABanWithNeitherUidNorName_IsRefused()
    {
        Assert.Equal("ban requires --uid <uid> or --player <name>", Refused("ban").Message);
    }

    [Fact]
    public void ADurationThatIsNotANumberOfSeconds_IsRefusedRatherThanDropped()
    {
        // Silently dropping it would turn "ban for an hour" into a permanent ban.
        Assert.Equal("--duration takes a number of seconds",
            Refused("ban", "--uid", "uid-1", "--duration", "1h").Message);
    }

    // ---- unban ----

    [Fact]
    public void AnUnban_TakesTheUidPositionallyOrUnderTheFlag()
    {
        Assert.Equal("""{"cmd":"unban","uid":"uid-1"}""", Payload("unban", "uid-1"));
        Assert.Equal("""{"cmd":"unban","uid":"uid-1"}""", Payload("unban", "--uid", "uid-1"));
        Assert.Equal("""{"cmd":"unban","uid":"uid-1","serverId":"creative"}""",
            Payload("unban", "uid-1", "--server", "creative"));
    }

    [Fact]
    public void AnUnbanWithNoUid_IsRefused()
    {
        Assert.Equal("unban requires --uid <uid>", Refused("unban").Message);
        // A flag sitting where the uid would go is a flag, not a uid named "--server".
        Assert.Equal("unban requires --uid <uid>", Refused("unban", "--server", "creative").Message);
    }

    // ---- whitelist ----

    [Fact]
    public void WhitelistWithNoSubVerb_Lists()
    {
        // Bare `whitelist` has to land on the harmless one rather than on an error or, worse, on
        // a mutation.
        Assert.Equal("""{"cmd":"whitelist-list"}""", Payload("whitelist"));
        Assert.Equal("""{"cmd":"whitelist-list"}""", Payload("whitelist", "list"));
        Assert.Equal("""{"cmd":"whitelist-list"}""", Payload("whitelist", "ls"));
    }

    [Fact]
    public void AWhitelistAdd_SendsWhatItWasGivenUnderTheAdminNames()
    {
        Assert.Equal("""{"cmd":"whitelist-add","uid":"uid-1"}""",
            Payload("whitelist", "add", "--uid", "uid-1"));
        Assert.Equal(
            """{"cmd":"whitelist-add","player":"builder","serverId":"staff","note":"trial","duration":86400}""",
            Payload("whitelist", "add", "--player", "builder", "--server", "staff",
                    "--note", "trial", "--duration", "86400"));
    }

    [Fact]
    public void AWhitelistAddAcceptsReasonWhereItMeansNote()
    {
        // Ban takes --reason and whitelist takes --note; an operator moving between the two
        // should not lose the text for typing the wrong one.
        Assert.Equal("""{"cmd":"whitelist-add","uid":"uid-1","note":"trusted"}""",
            Payload("whitelist", "add", "--uid", "uid-1", "--reason", "trusted"));
    }

    [Fact]
    public void AWhitelistAddWithNeitherUidNorName_IsRefused()
    {
        Assert.Equal("whitelist add requires --uid <uid> or --player <name>",
            Refused("whitelist", "add").Message);
        Assert.Equal("--duration takes a number of seconds",
            Refused("whitelist", "add", "--uid", "uid-1", "--duration", "forever").Message);
    }

    [Fact]
    public void AWhitelistRemove_TakesTheUidPositionallyOrUnderTheFlag()
    {
        Assert.Equal("""{"cmd":"whitelist-remove","uid":"uid-1"}""",
            Payload("whitelist", "remove", "uid-1"));
        Assert.Equal("""{"cmd":"whitelist-remove","uid":"uid-1","serverId":"staff"}""",
            Payload("whitelist", "rm", "--uid", "uid-1", "--server", "staff"));
        Assert.Equal("""{"cmd":"whitelist-remove","uid":"uid-1"}""",
            Payload("whitelist", "del", "uid-1"));
    }

    [Fact]
    public void AWhitelistRemoveWithNoUid_IsRefused()
    {
        Assert.Equal("whitelist remove requires --uid <uid>", Refused("whitelist", "remove").Message);
    }

    [Fact]
    public void AWhitelistSubVerbNobodyKnows_IsNamedBackWithTheOnesThatExist()
    {
        Assert.Equal("unknown whitelist sub-command: purge (add, remove, list)",
            Refused("whitelist", "purge").Message);
    }

    [Fact]
    public void AWhitelistSubVerbIsReadWithoutRegardToCase()
    {
        Assert.Equal("""{"cmd":"whitelist-add","uid":"uid-1"}""",
            Payload("whitelist", "ADD", "--uid", "uid-1"));
    }

    // ---- drain ----

    [Fact]
    public void Drain_TakesTheServerIdPositionallyOrUnderTheFlag()
    {
        Assert.Equal("""{"cmd":"drain","serverId":"creative"}""", Payload("drain", "creative"));
        Assert.Equal("""{"cmd":"drain","serverId":"creative"}""", Payload("drain", "--server", "creative"));
        Assert.Equal("""{"cmd":"undrain","serverId":"creative"}""", Payload("undrain", "creative"));
    }

    [Fact]
    public void DrainWithNoServerId_IsRefusedUnderTheVerbTheOperatorTyped()
    {
        Assert.Equal("drain requires <serverId> or --server <id>", Refused("drain").Message);
        Assert.Equal("undrain requires <serverId> or --server <id>", Refused("undrain").Message);
    }

    // ---- raw ----

    [Fact]
    public void Raw_SendsTheJsonItWasHandedWithoutRewritingIt()
    {
        // The point of raw is reaching an admin command nimctl has no verb for, so anything this
        // touched on the way through would defeat it.
        Assert.Equal("""{"cmd":"future-command","weird":[1,2]}""",
            Payload("raw", """{"cmd":"future-command","weird":[1,2]}"""));
    }

    [Fact]
    public void RawWithSomethingThatIsNotJson_IsRefusedBeforeItReachesTheSocket()
    {
        Assert.Equal("raw requires a JSON argument", Refused("raw").Message);
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => Program.BuildPayload(new List<string> { "raw", "{not json" }));
    }

    // ---- the alias table ----

    [Theory]
    [InlineData("?", "help")]
    [InlineData("ls", "list")]
    [InlineData("players", "list")]
    [InlineData("inspect", "status")]
    [InlineData("plugin", "plugins")]
    [InlineData("drop", "kick")]
    [InlineData("serverlist", "servers")]
    [InlineData("send", "swap")]
    [InlineData("transfer", "swap")]
    [InlineData("stickies", "sticky")]
    [InlineData("routes", "route")]
    [InlineData("resume", "undrain")]
    [InlineData("wl", "whitelist")]
    public void EveryAlias_ResolvesToTheVerbItStandsFor(string alias, string verb)
    {
        Assert.Equal(verb, Program.NormalizeCommand(alias));
    }

    [Fact]
    public void AVerbThatIsNotAnAlias_IsLeftAlone()
    {
        Assert.Equal("ban", Program.NormalizeCommand("ban"));
        Assert.Equal("frobnicate", Program.NormalizeCommand("frobnicate"));
    }

    [Fact]
    public void AnAliasReachesTheSameBuilderAsTheVerb()
    {
        // The table is only half the story: the alias has to come out the far end as the same
        // admin frame.
        Assert.Equal("""{"cmd":"list"}""", Payload("ls"));
        Assert.Equal("""{"cmd":"kick","id":5}""", Payload("drop", "5"));
        Assert.Equal("""{"cmd":"undrain","serverId":"creative"}""", Payload("resume", "creative"));
        Assert.Equal("""{"cmd":"whitelist-list"}""", Payload("wl"));
    }

    [Fact]
    public void ACommandNobodyRecognises_IsNamedBackToTheOperator()
    {
        Assert.Equal("unknown command: frobnicate", Refused("frobnicate").Message);
    }
}
