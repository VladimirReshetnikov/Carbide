# @carbide-ui/avalonia-runtime-bundle

Pre-built Avalonia.Browser WebAssembly runtime bundle for Carbide's cross-frame runner (plan Approach B). Bytes-only: no TypeScript or JavaScript public API. Consumed at UI-M3 by [`@carbide-ui/avalonia-runner`](../runner/README.md) (iframe-embedded) and at UI-M5 (Approach C, separate plan) by `@carbide/cli`'s `--target avalonia-browser` path.

## What's inside

After `npm run build` (maintainer-only; requires .NET 10 SDK + `wasm-tools` workload):

```
_framework/              # Published Avalonia.Browser + .NET runtime assets (~412 files).
shell/
    index.html           # Avalonia's <div id="out"> host page.
    main.js              # .NET boot module: imports _framework/dotnet.js and runs Main.
bundle-manifest.json     # { pinned, sizeBytes, framework[], shell[] } — SHA256 per file.
```

## Pinning

`bundle-manifest.json` records the triple-pin at build time:

```json
"pinned": {
  "avalonia": "12.0.1",
  "dotnet":   "10.0.6",
  "carbide":  null
}
```

The `carbide` slot is filled in at UI-M3 when the runner wires the `postMessage` protocol against a specific Carbide-core schema. Until then it is `null`.

Bumping `avalonia` or `dotnet` is a maintainer PR: edit the runner csproj PackageReference and/or SDK, run `npm run build`, commit the new `_framework/` tree and manifest. The drift workflow (plan §10.5) is engaged automatically if a minor bump changes the `framework[]` file list.

## Deployment considerations

The bundle ships **raw** `_framework/*.wasm` / `*.js` files plus **Brotli pre-compressed** `*.br` siblings. Gzip (`*.gz`) variants are intentionally **not** included — a modern browser downloads the `.br` (99%+ support), and any host that genuinely needs gzip can recompress the raw files at deploy time. This keeps the tarball under UI-I2's 35 MB compressed budget without hurting cold-load performance on static hosts.

Effective cold-load in a Brotli-capable browser: **~11 MB** (raw files are not fetched when `.br` is available). On-disk tarball: **~25 MB** (what `npm install` costs).

A consumer deploying to a host without static-compression middleware should either:

1. Serve the `.br` files with `Content-Encoding: br` when the `Accept-Encoding` header allows — most modern static hosts (Netlify, Cloudflare Pages, GitHub Pages, Vercel, nginx with the brotli module) do this transparently.
2. Or recompress the `_framework/` tree at deploy time via a build step.

## Smoke test

After `npm run build`, manually verify the bundle by serving the package root over HTTP and opening `test-shell.html`:

```bash
cd src/Carbide.UI/packages/runtime-bundle
python -m http.server 8000
# open http://localhost:8000/test-shell.html in a browser
# expected: "Carbide.UI runner — UI-M2 splash" renders in the canvas
```

`test-shell.html` is excluded from the published npm tarball (not listed in `package.json` → `files`). It exists for maintainer smoke only.

## Licence

Bundle contents redistribute Avalonia (MIT), Skia/HarfBuzz (MIT), and the .NET runtime (MIT) per their upstream licences. Third-party notices for the Avalonia DLLs are documented in [`@carbide-ui/refs-avalonia`'s `THIRD_PARTY_NOTICES.md`](../refs-avalonia/THIRD_PARTY_NOTICES.md); the same licence terms apply here.
