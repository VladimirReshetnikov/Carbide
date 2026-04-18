# @carbide/nuget

Bounded NuGet v3 resolver for Carbide. Downloads and extracts managed-only packages from an
allow-list, resolves transitive graphs with nearest-wins semantics, picks the best `lib/<tfm>/`
folder per the compatibility ladder, and hands raw PE bytes to Carbide's session reference
API. Ships with a filesystem cache and a lock file for deterministic offline replay.

Zero runtime dependencies — all parsing (zip, nuspec XML, version ranges) is hand-rolled.

## Quick start

```ts
import { resolve } from "@carbide/nuget";

const graph = await resolve(
    [{ id: "Newtonsoft.Json", versionRange: "[13.0.3]" }],
    { targetFramework: "net10.0", allowListMode: "strict" },
);

for (const ref of graph.references) {
    session.addReference(ref.bytes, ref.name);
}
```

The returned `ResolvedGraph` carries:

- `packages` — resolved `{id, version, sha256, requestedBy, dependencies, libFolder}` entries.
- `references` — `{name, bytes, packageId, packageVersion}`, one per chosen `lib/<tfm>/*.dll`.
- `warnings` — `{code, message, severity}` entries for the `MSNUGET0NN` codes.
- `lock` — a `ResolveLock` suitable for `writeLock(...)` and subsequent `--lock` replay.

## Allow-list (strict by default)

Carbide only resolves packages listed in `src/allowlist.ts`. The seed list is:

- `Newtonsoft.Json`, `YamlDotNet`, `CsvHelper`, `Humanizer.Core`, `NodaTime`, `Scriban`,
  `Handlebars.Net`, `Serilog`, `Serilog.Sinks.Console`, `FluentAssertions`.

Modes (via `ResolveOptions.allowListMode`):

- `strict` (default) — reject any package outside the list with `AllowListRefusedError`.
- `advisory` — resolve, but emit `MSNUGET020` warnings for unlisted packages.
- `off` — no check. Use for tests or one-shot experiments.

## Safety refusals

Packages carrying any of the following are rejected with `SafetyRefusalError`:

- `runtimes/<rid>/native/` — `MSNUGET015` (Mono-WASM can't load native binaries).
- `build/*.targets`, `build/*.props`, `buildTransitive/*.targets`, `buildTransitive/*.props`
  — `MSNUGET016` (Carbide does not execute MSBuild tasks).
- `analyzers/` — `MSNUGET017` (analyzer execution is a later milestone).

## Caching

Nupkgs are cached under `~/.carbide/nuget-cache/<id-lower>/<version>/` with a
`.carbide-meta.json` sidecar carrying the SHA-256. Every read verifies the digest; a
mismatch is treated as a cache miss. The location is overridable via
`ResolveOptions.cacheDir` or the `CARBIDE_NUGET_CACHE_DIR` environment variable.

## Lock file

`buildLock(...)` emits a deterministic, sorted `ResolveLock`. Pass it back via
`ResolveOptions.lock` to replay a resolution verbatim without any network calls. The lock
file's integrity check rejects tampered cache entries with `MSNUGET040`.

```json
{
  "schemaVersion": 1,
  "generator": "carbide",
  "generatedAt": "2026-04-18T22:19:10.000Z",
  "packages": [
    { "id": "Newtonsoft.Json", "version": "13.0.3", "sha256": "…", "requestedBy": ["<root>"], "dependencies": [], "libFolder": "netstandard2.0" }
  ],
  "warnings": []
}
```

## Warning codes (`MSNUGET0NN`)

| Code        | Meaning                                              |
|-------------|------------------------------------------------------|
| MSNUGET000  | Parse error (version, nuspec).                       |
| MSNUGET001  | Floating version (`1.*`, `*`) rejected.              |
| MSNUGET010  | Nearest-wins conflict (same-depth tie).              |
| MSNUGET015  | Safety refusal: native binaries.                     |
| MSNUGET016  | Safety refusal: MSBuild .targets/.props.             |
| MSNUGET017  | Safety refusal: Roslyn analyzers.                    |
| MSNUGET018  | Safety refusal: source generators.                   |
| MSNUGET019  | Safety refusal: other recognised hazard.             |
| MSNUGET020  | Allow-list advisory (unlisted, resolved anyway).     |
| MSNUGET021  | Allow-list refused (thrown as `AllowListRefusedError`). |
| MSNUGET030  | Cache miss under `--offline`.                        |
| MSNUGET031  | Cache read error / malformed meta.                   |
| MSNUGET040  | SHA-256 integrity mismatch (lock replay).            |

## CLI surface

`@carbide/cli` exposes the resolver via new flags on every command (`build`, `run`,
`validate`):

```
--offline                 Forbid network. Require cached bytes or a matching lock.
--lock <path>             Override lock path. Default <projectDir>/carbide.lock.json.
--no-lock-write           Skip writing the lock after a fresh resolve.
--nuget-source <url>      Override flat-container base URL. Default api.nuget.org.
--allow-list-mode <mode>  strict | advisory | off. Default strict.
```

When the project declares no `<PackageReference>`s, resolution is skipped entirely.

## Tests

- `npm test` — hermetic unit tests (version-range, TFM compat, nuspec, safety, allow-list,
  resolver with a mocked `FlatContainer`).
- `CARBIDE_NUGET_LIVE=1 npm run test:live` — live smoke against api.nuget.org: resolve
  Newtonsoft.Json 13.0.3 and prove lock replay is byte-identical.
