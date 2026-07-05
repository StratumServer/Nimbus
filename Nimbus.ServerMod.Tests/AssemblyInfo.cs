using Atlas.XUnit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Both dlls are staged flat into the embedded server's mods folder by the
// StageModUnderTest target: the ModLoader recognizes Nimbus.ServerMod.dll as a mod
// (assembly-level ModInfo attribute) and Nimbus.Shared.dll sits next to it so the
// runtime can resolve the dependency.
[assembly: AtlasMods("mod/Nimbus.ServerMod.dll", "mod/Nimbus.Shared.dll")]
