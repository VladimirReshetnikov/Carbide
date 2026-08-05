# Carbide drift report — 2026-08-05 (Roslyn 5.x evaluation)

Documentation in this directory is licensed under the repository's [Apache License 2.0](../../../LICENSE), with copyright held collectively by Carbide Contributors.

- Created (UTC): 2026-08-05
- Trigger: the [0.1.0 drift report](carbide-drift-report__2026-08-04__release-0.1.0.md) recommended
  evaluating the Roslyn `4.x` → `5.x` jump as its own workstream for `0.2.0`
- Scope: whether Carbide can move off the `4.14.0` hold, and what the attempt exposed

## Summary

**Roslyn `5.6.0` — the newest release — does not run on Carbide's Mono-WASM configuration.**
The first compilation of a run dies with a `StackOverflowException` whose stack is nothing but
`System.Threading.Volatile.ReadBarrier` calling itself. Bisecting the available `5.x` releases
puts the break between `5.3.0` and `5.6.0`:

| Roslyn | Result under Mono-WASM (Node host) |
| --- | --- |
| `4.14.0` (current pin) | passes |
| `5.0.0` | passes |
| `5.3.0` | passes |
| `5.6.0` | **`StackOverflowException` in `Volatile.ReadBarrier`** |

Getting to that answer required fixing a separate defect that would have made the whole
evaluation report a false result, and which silently affects *any* future dependency bump —
see finding 2. It is fixed and pinned.

**The `4.14.0` hold stands.** `5.3.0` validates cleanly and is a viable target if and when
the project wants it, but adopting it means pinning to a `5.x` whose own successor is broken,
so it buys currency without buying an upgrade path. That is a release-shape decision rather
than a maintenance one, and it is recorded here rather than taken.

## Finding 1 — Roslyn 5.6.0 self-recurses to a stack overflow

Publishing `Carbide.Core` against `Microsoft.CodeAnalysis.CSharp` / `.CSharp.Features`
`5.6.0` builds without error. The failure is at run time, on the first real compilation:

```text
Unhandled Exception:
StackOverflowException
[ERROR] FATAL UNHANDLED EXCEPTION: System.StackOverflowException: The requested operation caused a stack overflow.
at System.Threading.Volatile.ReadBarrier () [0x00005] in <5aa1d50d3b8945cfaf439c79bdb7f952>:0
at System.Threading.Volatile.ReadBarrier () [0x00000] in <5aa1d50d3b8945cfaf439c79bdb7f952>:0
... (every remaining frame identical)
```

The trace carries no caller frames — the recursion is `Volatile.ReadBarrier` invoking itself,
so the overflow consumes the whole stack before any Roslyn frame survives in it.
`Volatile.ReadBarrier` is a .NET 10 memory-model API that lives in the runtime's own CoreLib,
not in Roslyn; the reasonable reading is that the Mono-WASM implementation of it depends on
being replaced by an intrinsic, and that Roslyn `5.6.0` is the first component Carbide loads
that reaches it on a path where the substitution does not happen.

Two controls make this attributable to Roslyn rather than to the environment or to the change
that enabled the test:

- **Version control.** Reverting only the two `PackageReference` versions to `4.14.0` and
  republishing makes the identical test file pass all four of its cases. `5.0.0` and `5.3.0`
  likewise pass. The crash tracks the Roslyn version and nothing else.
- **Payload control.** Diffing the published `_framework/` between `4.14.0` and `5.6.0` shows
  the same asset set — 194 converted managed assemblies plus `dotnet.native.wasm`, which is
  copied verbatim from the runtime pack rather than converted — with size changes confined to
  the six `Microsoft.CodeAnalysis.*` assemblies and four transitive libraries
  (`System.Composition.Convention`, `System.Configuration.ConfigurationManager`,
  `System.Diagnostics.EventLog`, `System.Security.Cryptography.ProtectedData`). No runtime
  assembly changes, so this is not a case of the package graph swapping the Mono-WASM runtime
  out from under the test.

This is precisely the exposure the 0.1.0 report predicted when it declined to bump Roslyn in
the release: Carbide runs Roslyn on Mono-WASM, which upstream does not test.

**Status.** The pin stays at `4.14.0`. Re-test when either a newer `5.x` or a newer .NET 10
servicing runtime lands; the check is cheap now that the bisect procedure is written down.

## Finding 2 — the Webcil conversion cache survives a package-version change (fixed)

The first `5.6.0` run reported *success*. It was measuring the old compiler.

With `<WasmEnableWebcil>true</WasmEnableWebcil>`, the SDK converts every managed assembly to a
Webcil image under `obj/<config>/<tfm>/webcil/` and publishes from there. That conversion is
incremental on **file timestamps**: a converted `.wasm` is reused whenever it is newer than the
assembly it came from. A NuGet package restored at some earlier date carries that earlier
timestamp, so moving a `PackageReference` to a *different package version* leaves the previous
version's converted image in place and publishes it.

The result is a freshly built `Carbide.Core` shipped on top of the **previous** compiler, with
no error, no warning, and no change in asset names. The M8 note that "`dotnet publish` does not
clean its output dir" does not cover this: the staleness lives in `obj/`, so `rm -rf publish/`
does not clear it either. Here it survived a full clean publish, and the payload only became
genuinely `5.6.0` after `obj/.../webcil/` was deleted by hand — at which point
`Microsoft.CodeAnalysis.CSharp.wasm` moved from 6,687,001 to 7,003,417 bytes.

**Resolution.** `Carbide.Core.csproj` gains `CarbideInvalidateWebcilOnPackageGraphChange`,
which drops the converted output whenever `project.assets.json` moves. Restore rewrites that
file exactly when the resolved package graph changes, which is the invalidation the SDK's
timestamp check misses, and steady-state builds skip the target so the reconversion cost is
paid only when it is warranted.

Verified in both directions rather than only in the direction that was broken: bumping
`4.14.0` → `5.6.0` and `5.6.0` → `4.14.0` each fires the target and each produces the payload
matching the requested version, and a repeat publish with an unchanged graph does not fire it.

**Why this matters beyond Roslyn.** Every dependency bump in a Webcil-enabled project has this
failure mode, and its signature is a *passing* test suite — the worst possible presentation.
It also means any previously recorded "we bumped X and the suite stayed green" result is only
trustworthy if the payload was confirmed to have actually changed.

## Not observed

- No change to the pinned .NET SDK (`10.0.201`) or to the Mono WebAssembly runtime (`10.0.6`),
  so the vendored upstream notices stay version-accurate.
- Roslyn's vendored third-party notices are byte-identical between `4.14.0` and `5.6.0`
  (`ef46788…`), so a Roslyn move would not require re-vendoring them.

## Next report

Due when any pinned version changes, or at the next release, whichever comes first. The open
item this report leaves behind is the `5.6.0` stack overflow: re-test on the next `5.x` or the
next .NET 10 servicing runtime.
