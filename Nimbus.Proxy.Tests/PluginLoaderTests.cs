using System.Text.Json.Nodes;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The plugin loader, driven against real plugin assemblies built in this repository and loaded
/// through the collectible load context the proxy uses in production. Nothing is stubbed: the
/// dlls are copied into a scratch directory, discovered by the same Directory scan, resolved by
/// the same reflection, and constructed by the same Activator call.
///
/// What is asserted throughout is what an operator can see: which plugins end up in the list the
/// `plugins` admin command prints, and whether a loaded plugin's handlers actually run.
/// </summary>
public class PluginLoaderTests
{
    private const string SampleId = "hub-fallback";

    private static PluginLoader Loader(bool enabled = true, params string[] disabled)
        => new(new PluginLoaderOptions { Enabled = enabled, DisabledIds = disabled });

    // ---- discovery and the happy path ----

    [Fact]
    public void TheSamplePlugin_IsLoadedWithTheIdentityFromItsManifest()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        var plugin = Assert.Single(loader.Loaded);
        // The manifest wins over the type name for everything an operator sees in `plugins`.
        Assert.Equal(SampleId, plugin.Metadata.Id);
        Assert.Equal("Hub Fallback", plugin.Metadata.Name);
        Assert.Equal("0.4.0", plugin.Metadata.Version);
        Assert.Empty(plugin.Metadata.Dependencies);
        // The file it came from, so an operator can find the thing on disk.
        Assert.Equal(PluginDir.Sample, plugin.SourceFile);
        Assert.Equal(SampleId, plugin.Instance.Name);
    }

    [Fact]
    public async Task ALoadedPlugin_HasReallySubscribedByTheTimeLoadAllReturns()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        Loader().LoadAll(dir.Path, api);

        // The whole point of loading a plugin: its handler runs on the proxy's own event bus.
        // The sample plugin routes a player with no chosen backend to the hub.
        var evt = new PlayerChooseInitialServerEvent(new FakePlayer(), target: null);
        await api.Events.FireAsync(evt);

        Assert.Equal("hub", Assert.Single(api.Resolved));
        Assert.NotNull(evt.Target);
        Assert.Equal(42421, evt.Target!.Port);
        Assert.Equal("10.0.0.4", evt.Target.Host);
    }

    [Fact]
    public async Task ALoadedPlugin_LeavesARoutingDecisionSomethingElseAlreadyMade()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        Loader().LoadAll(dir.Path, api);

        var chosen = new ServerInfo { ServerId = "creative", Host = "10.0.0.9", Port = 42430 };
        var evt = new PlayerChooseInitialServerEvent(new FakePlayer(), chosen);
        await api.Events.FireAsync(evt);

        // A fallback that overrode an existing choice would not be a fallback. It does not even
        // ask the registry where the hub is.
        Assert.Same(chosen, evt.Target);
        Assert.Empty(api.Resolved);
    }

    [Fact]
    public async Task AFallbackPluginThatCannotFindItsBackend_LeavesRoutingAloneAndSaysSo()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        // No hub configured at all, which is what a misconfigured network looks like.
        var api = new FakeProxyApi();
        Loader().LoadAll(dir.Path, api);

        var evt = new PlayerChooseInitialServerEvent(new FakePlayer(), target: null);
        await api.Events.FireAsync(evt);

        Assert.Null(evt.Target);
        Assert.Contains(api.Logged, line => line.StartsWith("warn ") && line.Contains("hub backend was not found"));
    }

    // ---- the switches that stop a plugin loading ----

    [Fact]
    public void WithPluginsDisabled_ADirectoryFullOfPluginsIsNeverRead()
    {
        using var dir = PluginDir.With(PluginDir.Sample, PluginDir.BrokenShutdown);
        var loader = Loader(enabled: false);

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public void APluginNamedInTheDisabledList_IsNeitherLoadedNorSubscribed()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader(true, SampleId);

        loader.LoadAll(dir.Path, api);

        Assert.Empty(loader.Loaded);
        Assert.False(api.Events.HasSubscribers<PlayerChooseInitialServerEvent>());
    }

    [Fact]
    public void TheDisabledList_IsMatchedWithoutRegardToCase()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var loader = Loader(true, "HUB-Fallback");

        loader.LoadAll(dir.Path, new FakeProxyApi());

        // An operator typing the id with the wrong case in nimbus.toml still gets the plugin off.
        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public void AMissingPluginsDirectory_IsNotAnError()
    {
        var loader = Loader();

        // A fresh install has no plugins directory. That must start the proxy, not stop it.
        loader.LoadAll(Path.Combine(Path.GetTempPath(), "nimbus-no-such-dir-" + Guid.NewGuid().ToString("N")),
            new FakeProxyApi());

        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public void AnEmptyPluginsDirectory_LoadsNothing()
    {
        using var dir = PluginDir.Empty();
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public void OnlyTheTopLevelOfThePluginsDirectory_IsScanned()
    {
        using var dir = PluginDir.Empty();
        string nested = Path.Combine(dir.Path, "nested");
        Directory.CreateDirectory(nested);
        File.Copy(Path.Combine(PluginDir.With(PluginDir.Sample).Path, PluginDir.Sample),
            Path.Combine(nested, PluginDir.Sample));

        var loader = Loader();
        loader.LoadAll(dir.Path, new FakeProxyApi());

        // Plugins ship their dependencies in subdirectories; picking those up as plugins in their
        // own right is how a support library ends up being asked to be a plugin.
        Assert.Empty(loader.Loaded);
    }

    // ---- files that are not what they claim ----

    [Fact]
    public void AFileThatIsNotAnAssembly_IsSkippedAndTheRealPluginStillLoads()
    {
        using var dir = PluginDir.With(PluginDir.Sample).AddGarbageDll("truncated-download.dll");
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        // One bad file in the directory must not cost the operator every other plugin.
        Assert.Equal(SampleId, Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Fact]
    public void AnAssemblyWithNoPluginTypeInIt_ContributesNothingAndBreaksNothing()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        // A support library dropped in the plugins directory by mistake. Nimbus.Shared has no
        // IPlugin implementation anywhere in it.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Nimbus.Shared.dll"),
            Path.Combine(dir.Path, "Nimbus.Shared.dll"));

        var loader = Loader();
        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Equal(SampleId, Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Fact]
    public void TwoFilesClaimingTheSamePluginId_LoadOnce()
    {
        using var dir = PluginDir.With(PluginDir.Sample).Add(PluginDir.Sample, asName: "hub-fallback-copy.dll");
        // Both copies carry the shipped manifest, so both claim "hub-fallback".
        File.Copy(Path.Combine(dir.Path, "Nimbus.SamplePlugin.plugin.json"),
            Path.Combine(dir.Path, "hub-fallback-copy.plugin.json"), overwrite: true);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);

        var loader = Loader();
        loader.LoadAll(dir.Path, api);

        // An operator who forgot to delete the old version gets one plugin, not two copies of it
        // fighting over the same routing decision.
        Assert.Single(loader.Loaded);
    }

    [Fact]
    public async Task TwoCopiesOfAPlugin_DoNotBothSubscribe()
    {
        using var dir = PluginDir.With(PluginDir.Sample).Add(PluginDir.Sample, asName: "hub-fallback-copy.dll");
        File.Copy(Path.Combine(dir.Path, "Nimbus.SamplePlugin.plugin.json"),
            Path.Combine(dir.Path, "hub-fallback-copy.plugin.json"), overwrite: true);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);

        Loader().LoadAll(dir.Path, api);

        var evt = new PlayerChooseInitialServerEvent(new FakePlayer(), target: null);
        await api.Events.FireAsync(evt);

        // The duplicate was refused before it was constructed, so the hub is looked up once.
        Assert.Single(api.Resolved);
    }

    // ---- the manifest ----

    [Fact]
    public void WithNoManifest_TheIdComesFromTheFileAndTheNameFromTheType()
    {
        using var dir = PluginDir.With(PluginDir.Sample).DeleteManifest(PluginDir.Sample);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        var plugin = Assert.Single(loader.Loaded);
        Assert.Equal("Nimbus.SamplePlugin", plugin.Metadata.Id);
        Assert.Equal("HubFallbackPlugin", plugin.Metadata.Name);
        Assert.Equal("0.0.0", plugin.Metadata.Version);
        Assert.Equal(PluginLoader.CurrentApiVersion, plugin.Metadata.ApiVersion);
    }

    [Fact]
    public void AManifestThatIsNotJson_FallsBackToTheFileDefaultsRatherThanLosingThePlugin()
    {
        using var dir = PluginDir.With(PluginDir.Sample).WriteManifest(PluginDir.Sample, "{ not json at all");
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        // A fat-fingered manifest costs the plugin its metadata, not its load.
        var plugin = Assert.Single(loader.Loaded);
        Assert.Equal("Nimbus.SamplePlugin", plugin.Metadata.Id);
        Assert.Equal("0.0.0", plugin.Metadata.Version);
    }

    [Fact]
    public void AManifestThatIsJsonNull_FallsBackToTheFileDefaults()
    {
        using var dir = PluginDir.With(PluginDir.Sample).WriteManifest(PluginDir.Sample, "null");
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Equal("Nimbus.SamplePlugin", Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AManifestFieldThatIsBlank_FallsBackFieldByField(string blank)
    {
        using var dir = PluginDir.With(PluginDir.Sample)
            .PatchManifest(PluginDir.Sample, "id", blank)
            .PatchManifest(PluginDir.Sample, "version", blank);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        var plugin = Assert.Single(loader.Loaded);
        Assert.Equal("Nimbus.SamplePlugin", plugin.Metadata.Id);
        Assert.Equal("0.0.0", plugin.Metadata.Version);
        // The fields that were filled in are still honoured.
        Assert.Equal("Hub Fallback", plugin.Metadata.Name);
    }

    [Fact]
    public void ManifestValues_AreTrimmed()
    {
        using var dir = PluginDir.With(PluginDir.Sample).PatchManifest(PluginDir.Sample, "id", "  spaced-id  ");
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        // Untrimmed the id would fail the id charset check and the plugin would vanish.
        Assert.Equal("spaced-id", Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("slash/es")]
    [InlineData("semi;colon")]
    [InlineData("at@sign")]
    public void AManifestWithAnUnusablePluginId_SkipsThePlugin(string id)
    {
        using var dir = PluginDir.With(PluginDir.Sample).PatchManifest(PluginDir.Sample, "id", id);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        // An id is what an operator types to disable the plugin and what dependencies are named
        // by, so one that cannot be typed unambiguously is refused rather than mangled.
        Assert.Empty(loader.Loaded);
    }

    [Theory]
    [InlineData("hub.fallback")]
    [InlineData("hub_fallback")]
    [InlineData("hub-fallback2")]
    public void AnIdMadeOfTheAllowedPunctuation_IsAccepted(string id)
    {
        using var dir = PluginDir.With(PluginDir.Sample).PatchManifest(PluginDir.Sample, "id", id);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Equal(id, Assert.Single(loader.Loaded).Metadata.Id);
    }

    // ---- the api version gate ----

    [Fact]
    public void TheApiVersionKeyTheSampleSpells_IsTheOneThatIsRead()
    {
        // api_version, snake_case, exactly as every manifest in the wild spells it. Before this
        // was bound the value on disk was thrown away and the gate below compared the proxy's own
        // version against itself, which no plugin can fail.
        using var dir = PluginDir.With(PluginDir.Sample)
            .WriteManifest(PluginDir.Sample, """
                {"id":"hub-fallback","name":"Hub Fallback","version":"0.4.0","api_version":"0.0"}
                """);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Equal("0.0", Assert.Single(loader.Loaded).Metadata.ApiVersion);
    }

    [Fact]
    public void TheShippedSampleManifest_DeclaresTheApiThisBuildOffers()
    {
        // The sample is the only worked example a plugin author has, so it has to be a manifest
        // that loads. Bumping CurrentApiVersion past what it declares breaks here rather than in
        // somebody's plugins directory.
        using var dir = PluginDir.With(PluginDir.Sample);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Equal(PluginLoader.CurrentApiVersion, Assert.Single(loader.Loaded).Metadata.ApiVersion);
    }

    [Theory]
    [InlineData("0.1")]     // exactly what the proxy offers
    [InlineData("0.1.0")]   // the same, spelled with a patch component
    [InlineData("0.0")]     // older minor, still speakable
    public void APluginAskingForAnApiThisProxySpeaks_Loads(string apiVersion)
    {
        using var dir = PluginDir.With(PluginDir.Sample).PatchManifest(PluginDir.Sample, "api_version", apiVersion);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Equal(apiVersion, Assert.Single(loader.Loaded).Metadata.ApiVersion);
    }

    [Theory]
    [InlineData("1.0")]        // different major: a different set of types and signatures
    [InlineData("2.5")]
    [InlineData("0.2")]        // newer minor: the plugin wants members this proxy does not have
    [InlineData("not-a-version")]
    public void APluginAskingForAnApiThisProxyDoesNotSpeak_IsRefused(string apiVersion)
    {
        using var dir = PluginDir.With(PluginDir.Sample).PatchManifest(PluginDir.Sample, "api_version", apiVersion);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();

        string log = CapturingLog(() => loader.LoadAll(dir.Path, api));

        // Refused before construction: a plugin built against a different api surface would throw
        // a MissingMethodException somewhere unhelpful instead.
        Assert.Empty(loader.Loaded);
        Assert.False(api.Events.HasSubscribers<PlayerChooseInitialServerEvent>());
        // An operator staring at a plugin that stopped appearing needs the plugin, the version it
        // asked for and the version it was measured against, all three, in one line.
        Assert.Contains(SampleId, log);
        Assert.Contains(apiVersion, log);
        Assert.Contains(PluginLoader.CurrentApiVersion, log);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AManifestWithABlankApiVersion_IsTreatedAsNotDeclaringOne(string blank)
    {
        using var dir = PluginDir.With(PluginDir.Sample).PatchManifest(PluginDir.Sample, "api_version", blank);
        var loader = Loader();

        string log = CapturingLog(() => loader.LoadAll(dir.Path, new FakeProxyApi()));

        Assert.Equal(PluginLoader.CurrentApiVersion, Assert.Single(loader.Loaded).Metadata.ApiVersion);
        Assert.Contains("declares no api_version", log);
    }

    [Fact]
    public void APluginDeclaringNoApiVersion_LoadsAndIsWarnedAbout()
    {
        using var dir = PluginDir.With(PluginDir.Sample).RemoveManifestField(PluginDir.Sample, "api_version");
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();

        string log = CapturingLog(() => loader.LoadAll(dir.Path, api));

        // Nothing bound api_version until now, so plugins written without it were never refused
        // for it. Starting to refuse them the day the gate works would take working installs down
        // over a field nobody was told mattered. It loads, and the warning is the notice that it
        // will not keep loading forever.
        var plugin = Assert.Single(loader.Loaded);
        Assert.Equal(PluginLoader.CurrentApiVersion, plugin.Metadata.ApiVersion);
        Assert.Contains($"{SampleId} declares no api_version", log);
        Assert.Contains("will be required in a future release", log);
    }

    [Fact]
    public void APluginWithNoManifestAtAll_IsWarnedAboutTheSameWay()
    {
        using var dir = PluginDir.With(PluginDir.Sample).DeleteManifest(PluginDir.Sample);
        var loader = Loader();

        string log = CapturingLog(() => loader.LoadAll(dir.Path, new FakeProxyApi()));

        // No manifest is a manifest that declares no api version, and gets the same treatment.
        Assert.Single(loader.Loaded);
        Assert.Contains("declares no api_version", log);
    }

    /// <summary>Runs the loader with the proxy's log redirected and hands back what it wrote.</summary>
    private static string CapturingLog(Action action)
    {
        var captured = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(captured);
            action();
        }
        finally
        {
            Console.SetOut(previous);
        }
        return captured.ToString();
    }

    // ---- dependencies ----

    [Fact]
    public void APluginNamingADependencyNoFileProvides_IsSkipped()
    {
        using var dir = PluginDir.With(PluginDir.Sample)
            .PatchManifest(PluginDir.Sample, "dependencies", new JsonArray("some-library"));
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public void BlankEntriesInTheDependencyList_AreIgnoredRatherThanTreatedAsAMissingPlugin()
    {
        using var dir = PluginDir.With(PluginDir.Sample)
            .PatchManifest(PluginDir.Sample, "dependencies", new JsonArray("", "   "));
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        var plugin = Assert.Single(loader.Loaded);
        Assert.Empty(plugin.Metadata.Dependencies);
    }

    [Fact]
    public void APluginDependingOnOneThatWasDisabled_IsSkippedToo()
    {
        using var dir = PluginDir.With(PluginDir.Sample, PluginDir.BrokenShutdown)
            .PatchManifest(PluginDir.Sample, "dependencies", new JsonArray("Nimbus.ShutdownPlugin"));
        // Turning off a plugin has to turn off what leans on it, or the dependant loads against a
        // half-configured network.
        var loader = Loader(true, "Nimbus.ShutdownPlugin");

        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Empty(loader.Loaded);
    }

    // ---- plugins that fail on the way in ----

    [Fact]
    public void APluginWhoseInitializeThrows_IsNotListedAndTheOthersStillLoad()
    {
        using var dir = PluginDir.With(PluginDir.BrokenInit, PluginDir.Sample);
        var loader = Loader();

        loader.LoadAll(dir.Path, new FakeProxyApi());

        // Note: the broken plugin subscribes before it throws, and the loader has no way to take
        // that subscription back off the bus. What it does guarantee is that the plugin is not
        // reported as loaded and that the healthy one beside it is unaffected.
        var plugin = Assert.Single(loader.Loaded);
        Assert.Equal(SampleId, plugin.Metadata.Id);
    }

    [Fact]
    public void AnAbstractPluginType_IsWalkedPastRatherThanConstructed()
    {
        using var dir = PluginDir.With(PluginDir.BrokenInit);
        var loader = Loader();

        // Nimbus.BrokenPlugin holds an abstract base beside its concrete plugin. Trying to
        // construct the base would throw a MissingMethodException the loader would then blame on
        // the plugin author.
        loader.LoadAll(dir.Path, new FakeProxyApi());

        Assert.Empty(loader.Loaded);
    }

    // ---- shutdown ----

    [Fact]
    public void ShutdownAll_KeepsGoingPastAPluginThatThrows()
    {
        using var dir = PluginDir.With(PluginDir.BrokenShutdown, PluginDir.Sample);
        var loader = Loader();
        loader.LoadAll(dir.Path, new FakeProxyApi());
        Assert.Equal(2, loader.Loaded.Count);

        // A plugin throwing on the way out must not stop the proxy shutting down.
        loader.ShutdownAll();
    }

    // ---- reload ----

    [Fact]
    public void Reload_PicksUpAPluginDroppedInAndForgetsOneTakenAway()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var events = new EventBus();
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();
        loader.LoadAll(dir.Path, api);
        Assert.Equal(SampleId, Assert.Single(loader.Loaded).Metadata.Id);

        dir.Remove(PluginDir.Sample).Add(PluginDir.BrokenShutdown);
        loader.Reload(dir.Path, events, api);

        // The whole point of `nimctl reload`: the list follows the directory without a restart.
        Assert.Equal("Nimbus.ShutdownPlugin", Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Fact]
    public void Reload_RereadsTheManifestRatherThanRememberingTheOldOne()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();
        loader.LoadAll(dir.Path, api);
        Assert.Equal("0.4.0", loader.Loaded[0].Metadata.Version);

        // An operator dropping in a new build of the same plugin.
        dir.PatchManifest(PluginDir.Sample, "version", "0.5.0");
        loader.Reload(dir.Path, new EventBus(), api);

        Assert.Equal("0.5.0", Assert.Single(loader.Loaded).Metadata.Version);
    }

    [Fact]
    public async Task Reload_LeavesOneSubscriptionBehindRatherThanTwo()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();
        loader.LoadAll(dir.Path, api);

        loader.Reload(dir.Path, api.Events, api);

        var evt = new PlayerChooseInitialServerEvent(new FakePlayer(), target: null);
        await api.Events.FireAsync(evt);

        // The reload clears the bus before loading again. Without that, every reload would stack
        // another copy of every handler and the proxy would slow down one reload at a time.
        Assert.Single(api.Resolved);
        Assert.Equal(42421, evt.Target!.Port);
    }

    [Fact]
    public async Task Reload_TakesAwayTheHandlersOfAPluginThatIsGone()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();
        loader.LoadAll(dir.Path, api);

        dir.Remove(PluginDir.Sample);
        loader.Reload(dir.Path, api.Events, api);

        var evt = new PlayerChooseInitialServerEvent(new FakePlayer(), target: null);
        await api.Events.FireAsync(evt);

        // Deleting the dll has to stop the plugin acting, not just stop it being listed.
        Assert.Empty(loader.Loaded);
        Assert.Empty(api.Resolved);
        Assert.Null(evt.Target);
    }

    [Fact]
    public void Reload_CanTurnAPluginOffWithoutTouchingTheDirectory()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader();
        loader.LoadAll(dir.Path, api);
        Assert.Single(loader.Loaded);

        // plugins.disabled edited in nimbus.toml, then `nimctl reload`.
        loader.Reload(dir.Path, api.Events, api, new[] { SampleId });

        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public void Reload_CanTurnAPluginBackOn()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421);
        var loader = Loader(true, SampleId);
        loader.LoadAll(dir.Path, api);
        Assert.Empty(loader.Loaded);

        loader.Reload(dir.Path, api.Events, api, Array.Empty<string>());

        Assert.Equal(SampleId, Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Fact]
    public void Reload_SurvivesAPluginThatThrowsOnTheWayOut()
    {
        using var dir = PluginDir.With(PluginDir.BrokenShutdown);
        var api = new FakeProxyApi();
        var loader = Loader();
        loader.LoadAll(dir.Path, api);
        Assert.Single(loader.Loaded);

        // Reload shuts the old set down first. One plugin throwing there must not leave the
        // operator with a proxy that has unloaded everything and reloaded nothing.
        loader.Reload(dir.Path, api.Events, api);

        Assert.Equal("Nimbus.ShutdownPlugin", Assert.Single(loader.Loaded).Metadata.Id);
    }

    [Fact]
    public void Reload_WithPluginsTurnedOffEntirely_EmptiesTheList()
    {
        using var dir = PluginDir.With(PluginDir.Sample);
        var api = new FakeProxyApi();
        var loader = Loader(enabled: false);

        loader.Reload(dir.Path, api.Events, api);

        Assert.Empty(loader.Loaded);
    }

    // ---- what the operator sees ----

    [Fact]
    public async Task TheLoadedSet_IsWhatTheAdminPluginsCommandPrints()
    {
        using var dir = PluginDir.With(PluginDir.Sample, PluginDir.BrokenShutdown);
        var loader = Loader();
        loader.LoadAll(dir.Path, new FakeProxyApi().WithServer("hub", "10.0.0.4", 42421));

        await using var harness = await AdminHarness.StartAsync();
        harness.Plugins.AddRange(loader.Loaded);

        var reply = await harness.RunAsync(new { cmd = "plugins" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        var listed = reply.GetProperty("plugins").EnumerateArray().ToList();
        // Sorted by id and case-insensitively, so `nimctl plugins` reads the same way twice
        // running whatever order the directory scan handed them back in.
        Assert.Equal(new[] { SampleId, "Nimbus.ShutdownPlugin" }, listed.Select(p => p.GetProperty("id").GetString()));

        var sample = listed.Single(p => p.GetProperty("id").GetString() == SampleId);
        Assert.Equal("Hub Fallback", sample.GetProperty("name").GetString());
        Assert.Equal("0.4.0", sample.GetProperty("version").GetString());
        // The file to go and delete when the operator wants it gone.
        Assert.Equal(PluginDir.Sample, sample.GetProperty("source").GetString());
        Assert.Empty(sample.GetProperty("dependencies").EnumerateArray());
    }

    [Fact]
    public async Task WithNothingLoaded_ThePluginsCommandSaysSoRatherThanFailing()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "plugins" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Empty(reply.GetProperty("plugins").EnumerateArray());
    }
}
