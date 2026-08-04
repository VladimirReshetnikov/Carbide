# Public API surface reports

These reports are the compatibility freeze for Carbide's published packages (milestone M7).
Each file records the exported TypeScript surface of one package, rendered from its emitted
`.d.ts` output in a deterministic form.

They are generated — do not edit them by hand:

```powershell
node scripts/api-surface.mjs --write   # regenerate after an intentional API change
node scripts/api-surface.mjs           # check (CI gate; exits 1 on drift)
```

## What is frozen

| Report | Package | Surface |
| --- | --- | --- |
| [`carbide-core.api.md`](carbide-core.api.md) | `@carbide/core` | the `.`, `./node`, and `./interop/schema` entry points |
| [`carbide-cli.api.md`](carbide-cli.api.md) | `@carbide/cli` | the per-command flag specs, output formats, exit-code taxonomy, and error categories |
| [`carbide-msbuild-lite.api.md`](carbide-msbuild-lite.api.md) | `@carbide/msbuild-lite` | the `.` entry point |
| [`carbide-nuget.api.md`](carbide-nuget.api.md) | `@carbide/nuget` | the `.` entry point |

`@carbide/refs-net10.0` ships reference assemblies plus a `ref-manifest.json` rather than a
TypeScript surface, so it has no report here; its contract is the manifest shape, covered by
`@carbide/core`'s `ReferencePackDescriptor`.

Private class members are omitted: a consumer cannot reach them, so renaming one is not an
API change. A `private constructor()` is kept, because its visibility *is* contract.

## The contract

A pull request that changes any of these reports must:

1. Regenerate them with `node scripts/api-surface.mjs --write`.
2. Record the change in the affected package's `CHANGELOG.md`.
3. Treat a removal or an incompatible narrowing as a breaking change under
   [semantic versioning](https://semver.org/) — during `0.x` that means a minor bump.

The wire contract between the TypeScript and C# layers is versioned separately by
`SCHEMA_VERSION` (see `@carbide/core`'s `./interop/schema` entry point) and pinned by the
golden payloads under `Carbide/packages/core/test/fixtures/wire/`.

## License

These reports are part of Carbide and are licensed under the repository's
[Apache License 2.0](../LICENSE), with copyright held collectively by Carbide Contributors.
