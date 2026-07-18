# Carbide.UI — Avalonia Playground implementation plan

- Created (UTC): 2026-04-22T04:09:26Z
- Repository HEAD: 6c47c17a091454f44426c12a1ea7303bb8aca564

Status: **implementation plan for the `@carbide-ui/avalonia-playground` demo app.** A static-hosted single-page application that pairs a Monaco editor with a live Avalonia preview iframe, demonstrating the `@carbide-ui/*` family end-to-end. Builds on everything shipped in the [approach-B plan](carbide-ui-avalonia-approach-b-plan__2026-04-21__23-40-46-000000__d3b1a638db2c.md) (UI-M0..UI-M6 + core-P1..P3).

Audience: Carbide Contributors, and future contributors picking up phases.

Scope: the *how* of a cross-frame, edit-compile-run playground that runs entirely in a browser tab — no server, no .NET SDK. The "what" and "why" are settled: it's the marquee demo that makes the approach-B architecture concrete and shareable, directly paralleling [AvaloniaUI/XamlPlayground](https://github.com/AvaloniaUI/XamlPlayground) but with Carbide's distinctive "no-SDK-anywhere" property.

Out of scope:

- **Multi-tab playground** — the playground is one editor + one preview. A tab strip / multi-snippet playground is a post-v1 consideration.
- **Server-side compile** — pure client-side. This is the whole point.
- **Monaco's full IntelliSense** — C# semantic completion requires running Roslyn's completion service through the browser-hosted Carbide session, which is a separate integration (not in this plan).
- **NativeAOT precompilation** — user code stays interpreted (proposal §2 non-goal).

## 1. Context

### 1.1 What the approach-B tree already gives us

- `@carbide/core` compiles C# in the browser via Mono-WASM + Roslyn (`core-P1` sideload, `core-P2` collectible ALC, `core-P3` `BuildResult` fields).
- `@carbide-ui/refs-avalonia` supplies the compile-time Avalonia API surface (Avalonia 12.0.1).
- `@carbide-ui/avalonia-runtime-bundle` ships a prebuilt Avalonia.Browser `_framework/` tree and acts as the iframe src.
- `@carbide-ui/launcher` bridges a `BuildResult` into the iframe via `postMessage`.
- UI-M6 verified three concurrent Avalonia iframes in one `CarbideSession`. The playground needs only one iframe at a time, a considerably easier load.

The playground is **consumer code**, not framework code. It exercises the existing API surface rather than extending it. Any new API needs discovered during PG work become follow-up items against the approach-B plan, not the playground plan.

### 1.2 Reference architecture

```text
┌─────────────────────────────────────────────────────────────────┐
│  Playground page (single static HTML + JS + CSS bundle)          │
│                                                                  │
│  ┌────────────────────────┐  debounced   ┌──────────────────┐    │
│  │  Monaco editor         │   recompile  │  Preview iframe  │    │
│  │  (C# source)           │ ───────────► │  (Avalonia)      │    │
│  │                        │              │                  │    │
│  │  ← markers ← diags ←───┼────build─────┼──── launcher     │    │
│  │                        │              │                  │    │
│  └────────────────────────┘              └──────────────────┘    │
│                                                                  │
│  Toolbar: Run • Share • Sample picker • Ambient theme           │
│  Bottom:  diagnostic panel (collapsed by default)                │
│                                                                  │
│    CarbideSession (one per page; reused across recompiles)       │
│    + sideload: ["@carbide-ui/refs-avalonia"]                     │
└─────────────────────────────────────────────────────────────────┘
```

**Key architectural calls:**

- **One persistent `CarbideSession`** per page; recompiles go through `project.updateSource(...)`. Keeps WASM boot cost one-shot.
- **One persistent `Project`** likewise; no per-edit project churn.
- **Iframe-reboot on each successful recompile**, via `LaunchHandle.reload(build)` — the launcher already iframe-reboots under the hood (plan §7.3 v1 limitation).
- **Vite as the bundler**, because Monaco + TypeScript + static asset deployment work out of the box with it.

## 2. Invariants

Each phase re-asserts these.

- **PG-I1. Static-host-ready.** The `dist/` output is pure HTML/JS/CSS/binary assets. Any host that serves the tree with correct MIME types (including `application/wasm`) works. No server-side compile, no origin-tied cookies, no auth.
- **PG-I2. Single-origin.** The playground + Carbide framework + Avalonia bundle + runner iframe all load from the same origin. Cross-origin embedding is a post-v1 concern; CORS configuration is outside scope.
- **PG-I3. URL-shareable state.** After PG-P3 lands, every editor-visible state is encodable into the URL hash. No hidden per-tab state survives a URL paste.
- **PG-I4. Consumer-only.** The playground imports `@carbide/core` and `@carbide-ui/launcher` as public types; it does NOT reach into their internals. Any need for an internal helper surfaces as an approach-B follow-up.
- **PG-I5. No regression-facing runtime behaviour in shipped packages.** The playground may uncover bugs in `@carbide/core` / `@carbide-ui/*`; fixes land in those packages, not as workarounds in the playground.
- **PG-I6. Cold-load documented, not hidden.** First page load pulls ~11 MB Brotli (Carbide `_framework/` ~11 MB + Avalonia bundle ~11 MB effective when the iframe mounts). The playground surfaces a "loading..." indicator rather than pretending the page is instant.

## 3. Package layout

Placed under `src/Carbide.UI/playground/` — sibling to `packages/` and `samples/`. It's an **app**, not a reusable library, so it doesn't live under `packages/`.

```
src/Carbide.UI/playground/
  package.json            # @carbide-ui/avalonia-playground, private: true
  vite.config.mjs
  tsconfig.json
  index.html              # App shell
  src/
    main.ts               # Entry point: wires editor + preview + toolbar
    editor.ts             # Monaco wrapper: content, markers, diagnostics bridge
    preview.ts            # Launcher wrapper: session bootstrap + debounced recompile
    state.ts              # URL-hash + localStorage persistence
    samples.ts            # Built-in sample catalog (uses src/Carbide.UI/samples/)
    ui/
      toolbar.ts          # Run / Share / Sample picker / Theme toggle
      diagnostics.ts      # Bottom panel: error list with line/column + squiggle map
  public/
    favicon.svg
  README.md
```

**Dependencies (devDependencies only; `private: true` means nothing is published):**

- `@carbide/core` (workspace path `../../../Carbide/packages/core`)
- `@carbide-ui/launcher` (workspace path `../launcher`)
- `@carbide-ui/avalonia-runtime-bundle` (workspace path `../packages/runtime-bundle`)
- `@carbide-ui/refs-avalonia` (workspace path `../packages/refs-avalonia`)
- `monaco-editor` (~1 MB min-zip; Vite's `vite-plugin-monaco-editor` handles worker assets)
- `vite`, `typescript`

## 4. Phase dependencies

```text
PG-P1 ───► PG-P2 ───► PG-P3 ───► PG-P4
                │
                └─► PG-P5 (stretch; optional Playwright + polish)
```

Each phase is independently shippable. PG-P4 (deploy) is the "done" bar for a first public release.

## 5. PG-P1 — Scaffolding + compile-run loop

**Goal.** End-to-end "type C# → click Run → see Avalonia render". No polish.

### 5.1 Acceptance

- `npm run dev` in `src/Carbide.UI/playground/` starts Vite's dev server.
- The served page has a Monaco editor preloaded with the `hello-code-only` sample and an empty preview iframe.
- Clicking **Run** compiles the current editor buffer via a shared `CarbideSession` and `launchInIframe`s the resulting build. Compile errors alert-dialog their first error (polish lands in PG-P2).
- The iframe renders the sample's `TextBlock` within ~20 s of Run on a warm session.

### 5.2 File & package work

| Path | Contents |
|---|---|
| `package.json` | `private: true`, deps per §3, scripts: `dev`/`build`/`preview`. |
| `vite.config.mjs` | Monaco worker plugin; served at root; `server.fs.allow` extended so Vite can reach sibling packages in the monorepo. |
| `tsconfig.json` | `"module": "ES2022"`, `"moduleResolution": "Bundler"`, `"strict": true`. |
| `index.html` | Split-pane layout: left Monaco, right iframe. Minimal inline CSS. |
| `src/main.ts` | Wires editor + preview + toolbar. Single `CarbideSession` + `Project` created at boot. |
| `src/editor.ts` | `createEditor(container, initial): { getValue, setValue, setMarkers }`. |
| `src/preview.ts` | `createPreview(iframe): { compileAndRun(source, appClass) }`. Lazily initialises the session on first call. |
| `src/samples.ts` | Imports the three UI-M6 sample sources via Vite's `?raw` query; exposes `{name, appClass, source}`. |

### 5.3 Risks

- **PG-M1-R1.** Vite + Monaco worker assets: Monaco loads its own web workers via blob URLs by default; bundlers need explicit help. Mitigation: `vite-plugin-monaco-editor` (or the manual `?worker` import pattern); both are boilerplate.
- **PG-M1-R2.** Workspace-path deps in monorepo: `@carbide/core` isn't published. Use npm-install file paths (`"file:../../../Carbide/packages/core"`) or npm workspaces. Vite resolves file: URLs transparently; no bundler-specific tweak needed.
- **PG-M1-R3.** Carbide session init time on cold dev server: ~15 s on a warm dev server, more on cold. Acceptable for a "MVP works" checkpoint; PG-P2 adds a visible loading state.

## 6. PG-P2 — Diagnostics + auto-compile + sample picker

**Goal.** The playground *feels* alive: edits auto-compile, errors appear inline as red squiggles, and the user can flip between samples with a dropdown.

### 6.1 Acceptance

- Editor is idle 500 ms → playground calls `project.updateSource("App.cs", current)` and then `project.build()`. If successful, iframe reloads; otherwise diagnostics update without iframe churn.
- Compile errors appear as Monaco markers (red squiggles) on the exact span reported by each `diagnostic.lineStart/columnStart/lineEnd/columnEnd`.
- A collapsible diagnostics panel at the bottom of the page lists every error/warning with click-to-navigate-to-line.
- Sample-picker dropdown offers `hello-code-only`, `counter`, `hello-runtime-xaml-string`. Selection replaces editor content and triggers an immediate compile; the URL hash remains untouched (URL state is PG-P3).
- Auto-compile debouncing is observably 500 ms: typing a character does not trigger a recompile until the user pauses.

### 6.2 Design notes

- **Debounce state machine.** A single in-flight compile at a time. If a new edit arrives while a compile is running, remember the "latest" source; when the current compile finishes, start a new one with that source. Cancel the loading-iframe flow if diagnostics arrive in the interim.
- **Diagnostic → Monaco marker conversion.** `diagnostic.lineStart` is 1-based; Monaco is 1-based too (API `lineNumber`), so the mapping is direct. `spanStart`/`spanEnd` can be ignored for v1 — line/column is enough. Severity mapping: `"error" → MarkerSeverity.Error`, `"warning" → Warning`, `"hidden"/"info" → Hint`.
- **Iframe flicker.** Between a successful recompile and the new iframe mount, the iframe shows `about:blank`. Mitigate with a CSS transition on `opacity` from the previous content to the new; v1 accepts the flicker.
- **Save point.** Successful compiles snapshot the editor content to localStorage (`carbide-ui.playground.last-good`). A refresh restores the last-good source.

### 6.3 File & package work

| Path | Contents |
|---|---|
| `src/preview.ts` | Extended with debounced `scheduleRecompile`, in-flight tracking, and diagnostics callback. |
| `src/editor.ts` | `setMarkers(diagnostics)` method; `onDidChangeModelContent` hook. |
| `src/ui/diagnostics.ts` | Bottom panel: renders diagnostic list; click navigates editor. |
| `src/ui/toolbar.ts` | Sample picker `<select>` + Run button. Dropdown reuses `src/samples.ts`. |
| `src/state.ts` (new) | `saveLastGood(source) / loadLastGood(): string | null`. localStorage-only for P2. |

### 6.4 Risks

- **PG-M2-R1. `updateSource` cross-run state.** Whether recompiling the "same" project with updated source leaks types across runs (plan UI-R11) determines whether iframe-reboot (launcher's reload()) is enough. Worst case: new `Project` per compile. Mitigation: start with `updateSource`; fall back to new project if symptoms appear.
- **PG-M2-R2. Stale diagnostics during rapid typing.** If two compiles race, the user might see PG-P1 diagnostics on PG-P2 source. Mitigation: the debounce + in-flight tracking above enforces single-compile-at-a-time + "latest source wins".

## 7. PG-P3 — URL-hash state + share button

**Goal.** The user can share a playground URL that restores their source, their chosen sample (if any), and their theme preference.

### 7.1 Acceptance

- **Share** button in the toolbar serialises current editor content + `appClass` into the URL hash and copies the full URL to the clipboard. Toast-confirms the copy.
- Loading a URL with a hash of this shape decodes it back into the editor; auto-compile runs; iframe mounts.
- URL hash uses `#code=<base64url(gzip(<JSON>))>` where JSON is `{ v: 1, source, appClass }`. Version field reserves space for future additions (theme, sample id…).
- Decoding a v>1 or malformed hash shows a banner "This URL was produced by a newer playground version; load whatever I can" and falls back to the last-good localStorage source.
- Clearing the URL hash (e.g. by editing and not sharing) leaves the URL clean — no extra chars accumulate.

### 7.2 Design notes

- **gzip.** Use the browser's `CompressionStream("gzip")` + `DecompressionStream`; fall back to `pako` only if targeting browsers without these (modern Chromium/Firefox/WebKit all support them as of 2024).
- **Hash size.** A ~1 KB source compresses to ~0.4 KB gzipped → ~0.55 KB base64url. Browsers typically accept URL hashes well into the 100-KB range; no practical limit for any reasonable source size.
- **URL permalink vs. query.** Hash (`#`) not query (`?`) — hashes don't hit the server, preserving PG-I1.

### 7.3 File & package work

| Path | Contents |
|---|---|
| `src/state.ts` | Extended with `encodeState(state)` / `decodeState(hash): state | null` using `CompressionStream`. URL-hash read on boot; URL-hash write on Share. |
| `src/ui/toolbar.ts` | Share button. Clipboard API (`navigator.clipboard.writeText`) with user-gesture requirement satisfied by the click. |

### 7.4 Risks

- **PG-M3-R1.** `CompressionStream` support on older browsers: we target modern only (plan UI-M6 already constrains to Chromium-class behaviour). A graceful fallback to base64-only (no gzip) if `CompressionStream` is missing is a one-line addition.
- **PG-M3-R2.** Clipboard write requires a user gesture. Mitigation: the Share button click is always a user gesture. Silent background-write attempts (e.g. auto-copy on edit) are not in scope.

## 8. PG-P4 — Build, deploy, README

**Goal.** A production build lands in `dist/` and runs on a static host (GitHub Pages / Netlify / Cloudflare Pages / local `python -m http.server`). The playground's own README walks a new contributor from clone → deploy.

### 8.1 Acceptance

- `npm run build` produces a `dist/` tree that:
  - Is self-contained (all Carbide + Avalonia + Monaco assets copied/bundled into `dist/`).
  - Serves correctly via any static host that honours standard MIME types (most importantly `application/wasm` for `.wasm`).
  - Runs successfully when served via `python -m http.server 8000 --directory dist` on localhost.
- Total compressed `dist/` size is ≤ 50 MB (Carbide `_framework/` ~15 MB + Avalonia `_framework/` ~25 MB + Monaco + playground code ~4 MB).
- `README.md` in the playground root walks through: overview, dev-loop (`npm install && npm run dev`), build (`npm run build`), deploy (example for at least GitHub Pages), known limitations cross-linked to the approach-B plan.

### 8.2 Deployment target (open question, Q1)

**Recommendation: GitHub Pages.** Matches the repo's existing GitHub convention. Cloudflare Pages / Netlify are drop-in alternatives if the owner prefers. No deployment code lives in-tree; the README describes the manual publish step for v1.

If GitHub Pages is chosen:

- A workflow (if/when CI lands in this repo) can automate `npm run build` + `gh-pages` publish. Without CI, a single `npx gh-pages -d dist` from an authenticated clone suffices.

### 8.3 Risks

- **PG-M4-R1.** Total `dist/` size. 50 MB is large for a static demo but defensible given the WASM+framework payload. First-load users see a "loading" indicator for ~15–30 s on cold cache; subsequent visits are browser-cached.
- **PG-M4-R2.** Some static hosts default to serving `.wasm` with `application/octet-stream`. GitHub Pages and Cloudflare Pages handle `.wasm` correctly; Netlify does too. Document the MIME requirement prominently in the deploy README.

## 9. PG-P5 (stretch) — Playwright smoke + polish

Optional stretch phase; not a v1 blocker.

- Playwright spec: load the playground, type a one-line change, wait for the iframe to reload, assert a canvas has pixels. Exercises the full edit-compile-run loop.
- Keyboard shortcut: Ctrl+Enter to force a compile (bypass debounce).
- Theme toggle: editor light/dark, matching the OS `prefers-color-scheme` by default.
- "Reset to sample" button (distinct from sample-picker select): clears URL hash + localStorage + reloads from the current sample.
- Monaco semantic completion (very stretch): hook Roslyn completion through `@carbide/core`. Significant scope; probably a separate plan.

## 10. Cross-cutting concerns

### 10.1 Carbide session lifecycle

- One `CarbideSession` per page load. Created lazily on first compile. Reused across edits via `updateSource` + `build`.
- `LaunchHandle` is destroyed + recreated per successful build. Launcher already iframe-reboots (plan §7.3) — we just call `handle.reload(newBuild)` when one is alive, or `launchInIframe(...)` on the first run.
- Session shutdown on page unload (`beforeunload`): `session.shutdown()` releases the host adapter's HTTP server socket (Node only) and disposes the C# side. Non-critical for browser sessions but defensive.

### 10.2 Security considerations

- The user types arbitrary C#; Carbide compiles it; the runner loads it into a per-run collectible ALC (core-P2). The blast radius is the iframe. No secrets are visible to user code.
- CSP: the playground's own HTML needs `script-src 'wasm-unsafe-eval' 'self'` for its own JS + Carbide's WASM. The runner iframe has the same requirement independently.
- Clipboard: the Share button writes to `navigator.clipboard.writeText`. User-gesture-gated. No clipboard reads.

### 10.3 Performance budget

| Asset | Target (Brotli) | Notes |
|---|---:|---|
| Carbide `_framework/` | ≤ 15 MB | Same as UI-M3's effective cold load. |
| Avalonia runtime bundle | ≤ 11 MB | UI-M2 measurement. |
| Monaco (core + C# tokenizer) | ≤ 2 MB | Vite tree-shakes; exact value measured at PG-P4. |
| Playground's own JS/CSS | ≤ 500 KB | Mostly TS-compiled source. |
| **Total cold load** | **≤ 30 MB Brotli** | Documented in the playground README. |

Size gates land in PG-P4; earlier phases skip the hard gate in favour of "does it work" assertions.

### 10.4 Browser support

Matches `@carbide/core`'s + `@carbide-ui/*`'s support matrix:

- **Chromium** (Chrome, Edge, Opera) — fully supported and tested (UI-M6 Playwright).
- **Firefox** — Avalonia.Browser project supports it; not yet tested via the playground. Likely works.
- **WebKit (Safari)** — Avalonia.Browser project lists it as experimental. Untested in this stack. Documented as a known gap.

Mobile browsers: untested. iframe + WASM + large framework + canvas rendering on mobile is a distinct performance story; not in scope for v1.

## 11. Open questions

- **Q1 Deployment target.** GitHub Pages proposed; Cloudflare Pages / Netlify / self-hosted are drop-ins. **Owner decision.**
- **Q2 Branding.** "Avalonia Playground" (matches XamlPlayground naming) vs "Carbide + Avalonia Playground" (more descriptive). **Owner decision.** Affects page title, README header, maybe a domain/path.
- **Q3 Monaco vs CodeMirror.** Monaco ~2 MB Brotli, richer C# support. CodeMirror ~200 KB Brotli, simpler API. **Proposal: Monaco** — the playground's value rests on "feels like a real editor"; size is within cold-load budget.
- **Q4 Future extraction.** Should the playground eventually leave the monorepo for its own repo, thereby dogfooding the npm-install consumer path? **Position:** yes, post-v1, once `@carbide-ui/*` publishes to npm (currently `private: true`). Extraction is a clerical PR against this plan.
- **Q5 Sample curation.** Start with the three UI-M6 samples; expand to four once the `.axaml`-file sample lands from UI-M4's follow-up. **Default:** use whatever `src/Carbide.UI/samples/` exposes.

## 12. Risks (roll-up)

| # | Risk | Phase | Mitigation |
|---|---|---|---|
| PG-R1 | Monaco worker bundling across Vite/Webpack/esbuild. | PG-P1 | Use `vite-plugin-monaco-editor`; validate as part of PG-P1 acceptance. |
| PG-R2 | Iframe-reboot flicker on every recompile. | PG-P2 | CSS opacity transition; v1 accepts the flicker. |
| PG-R3 | Cold-load time UX. | PG-P4 | Loading indicator from first render; document the ~30 MB Brotli cost. |
| PG-R4 | `updateSource` cross-run state leakage. | PG-P2 | Fall back to new `Project` per compile if symptoms appear. Plan UI-R11. |
| PG-R5 | `CompressionStream` absent on target browser. | PG-P3 | Feature-detect and fall back to base64-only. Document. |
| PG-R6 | `dist/` size > 50 MB. | PG-P4 | Audit Monaco + Carbide tree for unused DLL bundles; reject if size stays out of budget. |
| PG-R7 | Clipboard write blocked by browser security. | PG-P3 | User-gesture-gated; fall back to `document.execCommand('copy')` if needed. |
| PG-R8 | WebGL context leak across many reloads. | PG-P2+ | Launcher's iframe-reboot disposes the previous context; validate in practice. |

## 13. Owner decisions required before PG-P1

1. **Q1 Deployment target.** (Low-risk; changeable at any phase.)
2. **Q2 Branding / page title.** (Trivial to change later.)

Everything else is delegable to the implementer.

## 14. Change control

- **Phase completion.** When PG-P<N> lands, mark the heading `✓ shipped (YYYY-MM-DD)` and link the shipping PR / commit.
- **Architectural re-opens.** Route through the approach-B plan's §12 or proposal §12 where applicable. This plan tracks the playground-specific decisions (deploy target, Monaco-vs-CodeMirror, etc.).
- **Stretch work.** PG-P5 items may land incrementally after PG-P4; no ordering constraint between them.
