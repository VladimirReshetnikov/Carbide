# carbide-pwsh browser interactive test infrastructure

- Created (UTC): 2026-04-25T22:33:27Z
- Updated (UTC): 2026-04-26T02:51:09Z
- Repository HEAD: 020144bd373ad7d9aa29914e84c14963d18d87f4
- Document type: current-state operational documentation
- Scope: `Carbide/packages/carbide-pwsh`

## Purpose

The `carbide-pwsh` browser endpoint is an interactive product surface, not only a library
API. The browser test infrastructure under `packages/carbide-pwsh/test/browser/` is
therefore designed to drive the same page a human opens, with the same xterm.js terminal,
the same browser-loaded Mono-WASM runtime, the same dynamically compiled shell sources,
and the same keyboard/paste input path.

The infrastructure deliberately avoids a C#-level or shell-internal shortcut. A test may
inspect the browser DOM, the xterm buffer, screenshots, page errors, and console logs, but
it should not call shell internals directly to create state that a user could not create
from the terminal.

## Components

`scripts/serve.mjs` remains the human-facing static server:

```bash
cd Carbide/packages/carbide-pwsh
node scripts/serve.mjs
```

The script now also exports:

- `createCarbidePwshStaticServer(...)`, which constructs the static-file server without
  binding a port.
- `startCarbidePwshStaticServer(...)`, which starts the server and returns the actual URL.
- `REPO_ROOT`, `DEMO_URL_PATH`, `DEFAULT_PORT`, and `guessMime(...)` for reuse by tests.

`test/browser/pwsh-browser-harness.mjs` owns the browser automation contract. It starts
the static server on an ephemeral port by default, launches Chromium through the
`@carbide/core` Playwright installation, opens the real `packages/carbide-pwsh/` page,
waits until the status element and xterm buffer show a ready pwsh prompt, and exposes a
small black-box shell driver:

- `sendLine(text, options)` types or pastes into xterm and presses Enter.
- `waitForText(text, options)` waits for text to appear in the xterm buffer.
- `waitForPrompt(options)` waits for a fresh `PS /home/user>` prompt.
- `textBuffer()` and `tail()` read the visible terminal transcript after ANSI stripping.
- `saveArtifacts(name, extra)` writes a screenshot, transcript, and JSON diagnostics under
  `Carbide/test-results/carbide-pwsh-browser/`.

If page startup fails before the shell is returned, `launchPwshBrowser()` closes the
Chromium instance and static server before rethrowing. This keeps failed or interrupted
startup attempts from leaking local helper processes.

`test/browser/dotnet-interactive.test.mjs` is the first consumer. It covers direct
browser `dotnet build`, direct DLL execution, and `dotnet run --project` from the public
pwsh prompt.

`test/browser/dotnet-nested-shells.test.mjs` extends that coverage to nested interactive
shells started from pwsh. It builds one probe assembly in the VFS and then verifies that
the same `dotnet` facade can execute it from nested `cmd`, nested `bash`, and Perl's
`perl -de 0` debugger pseudo-REPL.

The browser tests use Node's built-in test runner instead of `@playwright/test` so the
package does not need its own `node_modules`; the harness imports Playwright from
`packages/core/node_modules`.

`package.json` exposes:

```bash
npm run test:browser
npm run test:browser:dotnet
npm run test:browser:dotnet-nested
```

`test:browser` runs every `test/browser/*.test.mjs` file. `test:browser:dotnet` runs the
`dotnet-*` browser scenarios, including nested-shell coverage. `test:browser:dotnet-nested`
runs only the nested-shell scenarios. These scripts run with `--test-concurrency=1` because
each test owns a heavyweight browser + WASM shell session and concurrent launches can
obscure the failure signal.

## What Makes A Test Human-Like

The harness intentionally uses the real page and real xterm input:

- It starts from `index.html`, not from a synthetic fixture page.
- It waits for the dynamic browser compilation of `carbide-pwsh`.
- It clicks `#term` and sends data through xterm.
- It observes rendered terminal output, not C# object state.
- It captures xterm transcripts on failure so a maintainer can read the session like a
  user would have seen it.

The harness supports two input modes:

- The default mode uses `page.keyboard.type(...)`, which approximates a user typing.
- `entryMode: "paste"` calls xterm's paste path, which approximates a user pasting a
  command into the terminal. This is important for long code-generation commands such as
  `Set-Content ... -Value '...'`.

Both modes still enter through terminal input. They are not equivalent to calling
interpreter APIs directly.

## Why WinDesk Is Not In The Critical Path

WinDesk is useful when the target is a native Windows desktop surface or when automation
needs UIA, window capture, raw input, OCR, overlays, or co-control of existing desktop
applications. The `carbide-pwsh` browser endpoint exposes a first-class DOM and xterm.js
terminal inside Chromium, and Playwright can interact with that surface directly.

Using Playwright here has lower moving-part count:

- It can run headless in CI-style validation.
- It avoids visible desktop focus races.
- It avoids Chrome first-run/default-browser/profile prompts.
- It can read page errors, console logs, DOM state, xterm buffers, and screenshots in one
  automation layer.

WinDesk remains a plausible later complement for validating visible desktop co-control or
human-in-the-loop workflows, but it is not needed for the current black-box browser shell
tests.

## Prerequisites

The browser test depends on the same built artifacts as the human demo:

```bash
cd Carbide/packages/core
npm install
npm run build:dotnet
npm run build:ts

cd ../carbide-pwsh
dotnet build src/CarbidePwsh.csproj
npm run test:browser:dotnet
```

`npm install` is only required in `packages/core` because that package owns the
Playwright dependency and the browser-facing `dist/index.js` entrypoint.

## Artifact Contract

Failures call `saveArtifacts(...)`, which writes:

- `<prefix>.png` - full-page screenshot.
- `<prefix>.buffer.txt` - ANSI-stripped terminal transcript.
- `<prefix>.json` - URL, page errors, console logs, and scenario-specific diagnostics.

Artifacts live under `Carbide/test-results/carbide-pwsh-browser/`, which is ignored by
`Carbide/.gitignore`. They are intentionally local debugging evidence rather than
tracked fixtures.

## Invariants For Future Tests

- A browser interactive test should prefer `launchPwshBrowser()` over starting its own
  server or browser.
- A test should use `sendLine(...)` or explicit `page.keyboard` operations rather than
  invoking shell internals.
- A test that relies on text produced after a command should capture a `mark()` before the
  command and pass `since: mark` to `waitForText(...)` or `expectText(...)`.
- A failing test should save artifacts before rethrowing.
- A scenario that needs long source-code commands should exercise `entryMode: "paste"` so
  xterm paste and prompt-editor chunk handling stay covered.
- Nested-shell tests should start the child shell from the public pwsh prompt and exit back
  to pwsh before completing, rather than constructing nested shell state through internal
  APIs.

## Current Limitations

The harness validates behavior at the xterm transcript level. It does not yet model a
screen reader, native clipboard permissions, IME composition, browser zoom, touch input,
or focus handoff between multiple tabs. Those are reasonable future layers, but the
current contract is sufficient for detecting the main regressions that affect a human
typing or pasting commands into the public browser endpoint.
