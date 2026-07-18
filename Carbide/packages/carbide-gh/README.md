# carbide-gh

A Spectre.Console-powered `gh`-style GitHub REPL, compiled from C# in the browser and
run on Mono-WASM by [Carbide](../../README.md). The demo stresses the full interactive
stack:

- Roslyn compilation of four C# files plus a vendored `Spectre.Console.dll` reference
- async I/O via `await Console.In.ReadLineAsync()` (the line that used to trip T2.1)
- `HttpClient` against `https://api.github.com` through the Mono-WASM fetch bridge
- `System.Text.Json` round-tripping of the GitHub JSON payloads
- Spectre.Console `FigletText`, `Panel`, `Rule`, `Table`, `Tree`, `BarChart`, and a raw
  ASCII sparkline
- persistent session state (`CurrentRepo`, PAT, verbose flag) across commands

## License and third-party content

The demo's Carbide-authored code is licensed under [Apache-2.0](LICENSE). The vendored `Spectre.Console.dll` remains MIT-licensed; its copyright and permission notice are reproduced in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

## Running the demo

Prerequisites: the `@carbide/core` package must already be built — the demo fetches its
published `_framework/` directly from `/packages/core/src/bin/Release/net10.0/publish/`.

```bash
# One-time: vendor Spectre.Console.dll into public/lib/. Uses your local .NET SDK.
cd packages/carbide-gh
node scripts/fetch-spectre.mjs
```

Then serve:

```bash
node scripts/serve.mjs
# -> carbide-gh demo server: http://127.0.0.1:34570/packages/carbide-gh/
```

Open that URL in any modern browser.

## Commands

| command | what it does |
|---|---|
| `help`, `?` | show this panel |
| `repo owner/name` | set the current repo (all other commands read it) |
| `token ghp_… \| --clear` | set or clear the GitHub PAT (5000 req/hr vs 60) |
| `verbose [on\|off]` | toggle full exception traces |
| `prs [--state=open\|closed\|all]` | list pull requests |
| `pr <n>` | show one PR (panel + file tree) |
| `issues [--state=…] [--label=…]` | list issues |
| `issue <n>` | show one issue |
| `commits [--limit=N]` | list recent commits |
| `contributors` | bar chart of top committers |
| `stars [--pages=N]` | ASCII sparkline of stargazer growth |
| `clear`, `cls` | clear the screen |
| `exit`, `quit`, `:q` | exit the REPL |

## Smoke test

A headless Playwright driver lives at `scripts/smoke.mjs`. It boots the page, types
`help`, sets a repo, lists live PRs, and exits — failing if any of those steps don't
render the expected output. Requires `@carbide/core`'s `node_modules/playwright`.

```bash
node scripts/serve.mjs &
node scripts/smoke.mjs
# -> smoke: PASSED
```

Pass `SKIP_NETWORK=1` to skip the live `prs` check if you're rate-limited or offline.

## Why this exists

carbide-gh started life as a T2.1 investigation artifact — the "real consumer program"
that made the `PlatformNotSupportedException: Cannot wait on monitors on this runtime`
failure concrete. Every `await Console.In.ReadLineAsync()` in the REPL loop tripped the
Mono-WASM single-threaded monitor guard. The investigation commits and the frozen
pre-fix version of this directory live at
`../../docs/reports/artifacts/carbide-gh-T21-artifact/` for historical reference.

With T2.1 resolved, carbide-gh is the end-to-end exercise of the Carbide M1–T3 feature
stack: multi-document compilation, user DLL references, Roslyn async entry-point
invocation, the streaming interactive terminal, `HttpClient` through fetch, and the
forked System.Console cosmetic surface.

## Layout

| path | purpose |
|---|---|
| `index.html` | host page; loads xterm.js, `@carbide/core`, the vendored `Spectre.Console.dll`, compiles the four C# files, and calls `project.runInteractive({ terminal })`. |
| `src/Program.cs` | REPL entry point: banner + prompt + `while (true) { await Console.In.ReadLineAsync(); dispatch; }`. |
| `src/Commands.cs` | command dispatcher. |
| `src/GitHubClient.cs` | `HttpClient` wrapper for `https://api.github.com` + optional PAT auth. |
| `src/Render.cs` | Spectre.Console rendering helpers. |
| `public/lib/Spectre.Console.dll` | vendored DLL, Spectre.Console 0.49.1. |
| `scripts/fetch-spectre.mjs` | vendors `Spectre.Console.dll` via `dotnet restore` of a throwaway csproj. |
| `scripts/serve.mjs` | static server rooted at the Carbide repo root. |
| `scripts/smoke.mjs` | headless Playwright driver that asserts the demo runs end-to-end. |
