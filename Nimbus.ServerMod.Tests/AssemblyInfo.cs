using Atlas.XUnit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Staged as a folder mod by the StageModUnderTest target, the same shape release.yml
// ships: Nimbus.ServerMod.dll + Nimbus.Shared.dll + their deps.json next to a modinfo.json,
// so Nimbus.Shared.dll is a dependency inside the mod folder rather than a mod of its own.
[assembly: AtlasMods("mod/Nimbus.ServerMod")]
