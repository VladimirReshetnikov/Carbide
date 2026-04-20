# carbide-gh — a Spectre.Console GitHub REPL in the browser

A tiny demo showcasing what [Carbide](../../README.md) T3 unlocked: pre-compiled NuGet libraries that call stock `Console.*` now work unmodified in the browser. This one runs a four-file C# REPL (compiled in your browser on a Mono-WASM Roslyn) that fetches from `https://api.github.com` and renders the output with [Spectre.Console](https://spectreconsole.net) — tables, tree views, bar charts, panels, figlet banners, markup strings — straight into an xterm.js terminal.

## Setup

From the Carbide repo root:

```bash
# 1. Build + publish @carbide/core so the forked _framework/ exists.
cd packages/core
npm run build

# 2. Vendor Spectre.Console.dll into the demo. Uses your local .NET SDK.
cd ../../examples/carbide-gh
node scripts/fetch-spectre.mjs

# 3. Serve the demo.
node scripts/serve.mjs
# → carbide-gh demo server: http://127.0.0.1:34570/examples/carbide-gh/
```

Open the URL in a modern browser. First load fetches ~35 MB of Mono-WASM runtime + BCL assemblies (cached afterwards) and compiles the REPL in around a second.

## Commands

| Command | What it does |
|---|---|
| `help`, `?` | Show the command panel. |
| `repo owner/name` | Set the current repo. All other commands read it. |
| `token ghp_…` / `token --clear` | Attach a GitHub PAT (lifts the 60 req/hr anonymous limit to 5000). |
| `verbose [on\|off]` | Toggle full exception traces on errors. |
| `prs [--state=open\|closed\|all]` | Table of pull requests. |
| `pr <n>` | One PR: header, description panel, tree of changed files. |
| `issues [--state=…] [--label=…]` | Table of issues. |
| `issue <n>` | One issue: header + body panel. |
| `commits [--limit=N]` | Table of recent commits. |
| `contributors` | Bar chart of top committers. |
| `stars [--pages=N]` | ASCII sparkline of stargazer growth (≤100 stars per page). |
| `clear` / `cls` | Clear the screen (`\x1b[2J\x1b[H`). |
| `exit` / `quit` / `:q` | Exit the REPL. |

## Try it

```text
gh › repo anthropics/claude-code
gh (anthropics/claude-code) › prs
gh (anthropics/claude-code) › pr 33297
gh (anthropics/claude-code) › contributors
gh (anthropics/claude-code) › stars
```

## Current limitations

- **Line input only.** Arrow-key navigation / selection prompts would need `Console.ReadKey` on an async path, which is still PNS on Mono-WASM browser (Carbide's T2.1 regression). The REPL sidesteps that by committing input with Enter.
- **Stars endpoint is paginated.** `stars` fetches up to 4 pages (400 stars) by default. For repos with 10k+ stars the sparkline is a sample of the most-recent window, not the full history. Bump with `stars --pages=20` up to GitHub's cap.
- **No GUI for the token.** You paste the PAT into the xterm via `token ghp_…`. It lives in memory for the session only — no persistence, no cookie, no localStorage.
- **Spectre.Console's `Status` / `Progress` aren't wired.** Their spinner frames tick via `Task.Delay`, which PNS's on Mono-WASM browser. The REPL prints a plain `[dim]fetching…[/]` line instead.

## How it fits Carbide

- `@carbide/core` is loaded from the sibling `packages/core/dist/` and its published `_framework/` (so this demo re-uses the real overlay — our forked `System.Console.dll` is what runs Spectre's output path).
- The four `.cs` files are handed to `Project.addSource` as a Carbide M2 multi-document project.
- `Spectre.Console.dll` (vendored into `public/lib/` at setup time) is handed to `session.addReference(bytes)` as an M3-style user reference.
- `Project.runInteractive({ terminal })` streams output into xterm through the T1+T2+T3 bridge; `Console.In.ReadLineAsync()` lands on the T2 `BrowserTerminalReader`.

See also:
- [Carbide current-state guide](../../docs/Carbide-Current-State-Guide.md) for the full feature matrix.
- [T3 detailed plan](../../docs/planning/milestones/carbide-T3-detailed-plan__2026-04-20__13-56-27-000000.md) for what the forked `System.Console.dll` does and doesn't cover.
