# Contributing to Nimbus

Contributions are welcome, from typo fixes to design discussions. This page is the
practical part: how the branches work, how changes land, and how to run the tests.

## Branch model

- **`indev`** is the integration branch. Every PR targets it.
- **`main`** is release-stable. It only receives merges from `indev`.
- Releases are cut by pushing a `v*` tag (see `.github/workflows/release.yml`); the tag
  must match the versions in `Nimbus.ServerMod/AssemblyInfo.cs` and
  `Nimbus.Shared/NimbusProtocol.cs` or the pipeline refuses to ship.

## How changes land

Everything lands as a PR, even trivial changes, so history stays traceable. The review
rhythm (so the project keeps moving without cutting anyone out of review):

- **Low-risk PRs** (tests, docs, inert infra): land after **3 days** if nobody flagged
  anything.
- **Features and behavior changes**: stay open **7 days**, or land earlier with an
  approval.
- Review after landing is still review: post-merge feedback gets a revert or a follow-up,
  no ego attached.

Good entry points are labeled
[`good first issue`](https://github.com/StratumServer/Nimbus/labels/good%20first%20issue)
and [`help wanted`](https://github.com/StratumServer/Nimbus/labels/help%20wanted); the
direction lives on the roadmap issues. Ping @Pixnop to scope anything together.

## Building

Requires the **.NET 10 SDK**.

```shell
dotnet build Nimbus.slnx -c Release
```

`Nimbus.ServerMod` resolves the game dlls relative to the repo's parent directory:
`../../bin/<Config>/net10.0/VintagestoryAPI.dll` (the `.pdb` next to it is required, the
engine reads it at boot) and `../../.vanilla/Lib/protobuf-net.dll`. Mirror that layout
from a Vintage Story install (symlinks are fine).

## Testing

Three suites, all run by CI on every PR:

| Suite | Needs | Runs in |
|-------|-------|---------|
| `Nimbus.Registry.Core.Tests` | ASP.NET runtime | milliseconds |
| `Nimbus.Proxy.Tests` | ASP.NET runtime | milliseconds |
| `Nimbus.ServerMod.Tests` | a **Vintage Story 1.22.x install**, `VINTAGE_STORY` pointing at it | ~30s (boots a real embedded server via [Atlas](https://github.com/Pixnop/Atlas)) |

```shell
VINTAGE_STORY=/path/to/vintagestory dotnet test Nimbus.ServerMod.Tests
dotnet test Nimbus.Proxy.Tests
dotnet test Nimbus.Registry.Core.Tests
```

Behavior changes come with tests; the existing suites show the house style (real
loopback HTTP against a fake registry, independent reimplementations for anything
security-relevant, no sleeps in time-dependent tests).

## License note

The project is source-available under its custom [LICENSE](LICENSE); by contributing you
agree your contribution is licensed under the same terms.
