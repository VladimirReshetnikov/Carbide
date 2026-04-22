# @carbide-ui/refs-avalonia

Compile-time Avalonia reference assemblies for Carbide. This package is the Avalonia-shaped companion to [`@carbide/refs-net10.0`](../../../Carbide/packages/refs-net10.0/README.md): it ships the DLLs Roslyn needs to see when compiling Avalonia-referencing C# under `@carbide/core`.

## What's inside

After `npm install` (or `npm run build`), the package contains:

```
ref/net10.0-browser/     # Avalonia reference DLLs, 8 entries.
refpack.json             # { schemaVersion, avaloniaVersion, sources, dlls: [{ name, sha256, sizeBytes, sourceId }] }
```

The ref tree is populated by [`scripts/build.mjs`](scripts/build.mjs), which downloads the pinned Avalonia nupkgs from nuget.org, verifies them, and extracts the specific DLLs Carbide consumers need. Only the `lib/<tfm>/` content is pulled — build tasks, analyzers, designers, and native runtime bits are filtered out per plan UI-I8.

## DLL manifest (v12.0.1)

| DLL | Source nupkg | Size |
|---|---|---:|
| `Avalonia.dll` | avalonia | 4 KB |
| `Avalonia.Base.dll` | avalonia | 2.16 MB |
| `Avalonia.Controls.dll` | avalonia | 1.24 MB |
| `Avalonia.Markup.dll` | avalonia | 44 KB |
| `Avalonia.Markup.Xaml.dll` | avalonia | 73 KB |
| `Avalonia.Markup.Xaml.Loader.dll` | avalonia.markup.xaml.loader | 544 KB |
| `Avalonia.Browser.dll` | avalonia.browser | 213 KB |
| `Avalonia.Themes.Fluent.dll` | avalonia.themes.fluent | 696 KB |

Total extracted ≈ 4.93 MB; compressed tarball ≈ 1.64 MB. Budgets per plan UI-I2: ≤ 5 MB uncompressed, ≤ 2 MB compressed.

## Pinning

`scripts/build.mjs` pins `AVALONIA_VERSION`. Bumps are deliberate PRs:

1. Change `AVALONIA_VERSION` at the top of `build.mjs`.
2. Run `npm run build` — the script re-downloads, regenerates `ref/`, and rewrites `refpack.json` with the new SHA256s.
3. Review the `refpack.json` diff; commit both the script change and the new manifest. The `ref/` tree is `.gitignored` and regenerates at `postinstall`.
4. Run `node ../../scripts/measure-sizes.mjs` to confirm UI-I2 budgets.

Bumping the patch (12.0.z) is routine. Bumping the minor (12.y) or moving to 13.x engages the drift workflow (plan §10.5) — flag any API-surface changes that would affect existing Carbide-authored Avalonia samples.

## Consumption

Once [`CarbideOptions.sideload`](../../../Carbide/docs/planning/carbide-ui-avalonia-approach-b-plan__2026-04-21__23-40-46-000000__d3b1a638db2c.md) (plan core-P1, §3.2) lands in `@carbide/core`:

```ts
const session = await CarbideSession.initializeAsync({
    sideload: ["@carbide-ui/refs-avalonia"],
});
const project = session.createProject();
project.addSource("App.cs", /* code that references Avalonia */);
const result = await project.build();
```

Before core-P1 lands, consumers can feed the refs manually by reading `refpack.json`, loading each DLL's bytes, and calling `session.addReference(bytes, dll.name)`.

## Licence and redistribution

Avalonia is MIT-licensed by the AvaloniaUI project; the relevant notice is reproduced in [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md). `@carbide-ui/refs-avalonia` itself is Apache-2.0; the redistributed DLLs are governed by Avalonia's MIT terms.
