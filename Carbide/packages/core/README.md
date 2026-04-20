# @carbide/core

- Status: Informational (package README and API usage guide)
- Audience: Users embedding Carbide in Node tools or browser apps; maintainers extending the runtime
- Scope: The `@carbide/core` session/project API and host-adapter model
- Created (UTC): 2026-04-19T00:00:35Z
- Repository HEAD: d2f6eb2b29127011a7f7d713607bdfb4861c2b5f
- Related code:
  - `src/ts/session.ts` (`CarbideSession`)
  - `src/ts/project.ts` (`Project`)
  - `src/ts/host/` (Node and browser host adapters)
  - `src/CompilationInterop.cs` (JSExport surface)
  - `src/Services/ProjectCompiler.cs` (Roslyn compile, emit, and run)
- Related docs:
  - [Carbide README](../../README.md)
  - [Carbide current-state guide](../../docs/Carbide-Current-State-Guide.md)
  - [@carbide/cli](../cli/README.md) (subprocess-friendly wrapper)
  - [@carbide/msbuild-lite](../msbuild-lite/README.md) (bounded `.csproj` parser)
  - [@carbide/nuget](../nuget/README.md) (bounded NuGet resolver)
  - [@carbide/refs-net10.0](../refs-net10.0/README.md) (recommended ref-pack)

## Summary

`@carbide/core` is the runtime heart of Carbide: a small TypeScript API that boots a Mono-WASM
.NET runtime and drives Roslyn inside it. The public surface is intentionally narrow:

- create a `CarbideSession` (boots the runtime)
- create one or more `Project`s inside that session
- add/update/remove source documents
- optionally attach user-supplied reference DLL bytes
- `getDiagnostics()`, `build()` (emit PE/PDB bytes), or `run()` (compile + execute)

## Install

```bash
npm install @carbide/core
```

For a stable compile-time BCL surface (recommended), also install the ref-pack sibling:

```bash
npm install @carbide/refs-net10.0
```

Without the ref-pack, Carbide compiles against the runtime's packaged DLLs, which are trimmed
for size and can be missing members that your code expects.

## Quick start (Node)

```ts
import { CarbideSession } from "@carbide/core";

const session = await CarbideSession.initializeAsync();
try {
    const project = session.createProject({ assemblyName: "HelloApp" });
    project.addSource("Program.cs", 'Console.WriteLine("hello from Carbide");');

    const result = await project.run();
    console.log(result.stdOut);
} finally {
    await session.shutdown();
}
```

## Multi-document editing

```ts
const session = await CarbideSession.initializeAsync();
try {
    const project = session.createProject({ assemblyName: "GreeterApp" });
    project.addSource("Greeter.cs", "namespace Demo; public static class Greeter { public static string Greet(string name) => $\"hello, {name}\"; }");
    project.addSource("Program.cs", "using Demo; Console.WriteLine(Greeter.Greet(\"world\"));");
    const first = await project.run();

    project.updateSource("Greeter.cs", "namespace Demo; public static class Greeter { public static string Greet(string name) => $\"hi, {name}\"; }");
    const second = await project.run();
} finally {
    await session.shutdown();
}
```

Document identity is the `path` string you pass. Paths are compared byte-for-byte; casing and
slash direction matter.

## User-supplied reference DLLs

`CarbideSession.addReference(bytes)` registers reference bytes on the session and returns an
opaque handle. Attach it to one or more projects via `project.addReference(handle)`.

```ts
import { readFileSync } from "node:fs";
import { CarbideSession } from "@carbide/core";

const session = await CarbideSession.initializeAsync();
try {
    const bytes = new Uint8Array(readFileSync("./MyHelper.dll"));
    const handle = session.addReference(bytes, "MyHelper");

    const project = session.createProject({ assemblyName: "UseHelper" });
    project.addReference(handle);
    project.addSource("Program.cs", "using MyHelper; Console.WriteLine(Thing.Describe(42));");
    const result = await project.run();
} finally {
    await session.shutdown();
}
```

Handles are session-scoped. A handle from session A cannot be attached to a project created
by session B. Removing a reference or shutting down the session disposes the handle.

## API reference

### `CarbideSession.initializeAsync(options?)`

Accepts:

- `hostAdapter?: HostAdapter` - override host auto-detection and control where runtime assets come from.
- `debugLevel?: number` - passed to `dotnet.js` host config.
- `enableDiagnosticTracing?: boolean` - enables Mono diagnostic tracing (very verbose).

When omitted, Carbide auto-picks a host adapter:

- Node: uses `NodeHostAdapter` (HTTP asset delivery by default)
- Browser: uses `BrowserHostAdapter` derived from `import.meta.url`

### `session.createProject(options?)`

`options` (`ProjectOptions`) currently includes:

- `assemblyName?: string`
- `rootNamespace?: string` (currently informational)
- `languageVersion?: string` (passed through to Roslyn parse options when parseable)
- `nullable?: boolean` (enables nullable context globally)
- `implicitUsings?: boolean` (default true; injects a hidden `Carbide.GlobalUsings.g.cs`)
- `defineConstants?: string[]` (preprocessor symbols)
- `targetFramework?: "net8.0" | "net10.0"` (current codebase is optimized for `net10.0`; this is not yet a full TFM selector)

### `project` operations

- `addSource(path, code)`
- `updateSource(path, code)`
- `removeSource(path)` (no-op if not present)
- `addReference(handle)`
- `getDiagnostics()`
- `build()` -> `{ success, pe?, pdb?, diagnostics, durationMs }`
- `run()` -> `{ success, stdOut, stdErr, exitCode?, diagnostics, durationMs, ... }`
- `runInteractive(options)` -> `TerminalSession` (browser only; see "Browser interactive terminal")

## Browser interactive terminal

T1 — `project.runInteractive(options)` streams stdout/stderr into an xterm.js-shaped
`Terminal` while the program runs, instead of buffering the whole transcript for
end-of-run retrieval. It's a sibling of `project.run()`, not a mode flag on it —
`run()` keeps its existing buffered semantics unchanged.

```ts
import { Terminal } from "@xterm/xterm";
import { CarbideSession, BrowserHostAdapter } from "@carbide/core";
import "@xterm/xterm/css/xterm.css";

const adapter = new BrowserHostAdapter({
    frameworkAssetsBaseUrl: "/path/to/_framework/",
});
const session = await CarbideSession.initializeAsync({ hostAdapter: adapter });
const project = session.createProject({ assemblyName: "Demo" });

project.addSource(
    "Program.cs",
    'Console.WriteLine("\\x1b[1;33mhello\\x1b[0m from Carbide");',
);

const terminal = new Terminal({ cols: 80, rows: 24 });
terminal.open(document.getElementById("term")!);

const run = project.runInteractive({ terminal });
const result = await run.exitPromise;       // exitCode, stdOut, stdErr, diagnostics
console.log("program exited with", result.exitCode);

await session.shutdown();
```

Options:

- `terminal` (required) — any object implementing `write(data: string | Uint8Array)`.
  The xterm.js `Terminal` class satisfies this; tests often pass a lightweight mock.
- `args` — forwarded to the entry point's `Main(string[])`, same shape as
  `RunOptions.args`.
- `stderrStyle` — `"plain"` (default), `"dim"`, or `"red"`. Applied as an SGR wrap
  around each stderr flush chunk before it reaches `terminal.write`.

`runInteractive` returns a `TerminalSession` with:

- `exitPromise: Promise<RunResult>` — resolves when the user program exits. Never
  rejects; crashes surface as `success: false` with populated `stdErr`.
- `dispose(): Promise<void>` — idempotent teardown; safe to call mid-run.

Supported in T1: streaming stdout/stderr, ANSI passthrough, SGR stderr wrap,
`Console.OpenStandardOutput()` routes to the terminal via the `print` overlay
(previously went to the browser devtools console — see the T1 drift entry).

Out of scope for T1: stdin (`Console.ReadLine`, `Console.ReadKey`), color / cursor
/ window-size API parity, Ctrl+C. All of that lands in T2 via `CarbideConsole.*Async`.

Not available on the Node adapter — `project.runInteractive` throws if called on a
Node-backed session. The CLI has no `--interactive` flag; interactive terminals are
a browser-only feature.

## Host adapters

Carbide isolates Node vs browser boot differences behind a small `HostAdapter` interface:

- `resolveFrameworkAssetsBaseUrl()` - base URL for fetching metadata DLLs and other `_framework/` assets.
- `resolveDotnetModuleUrl?()` - base URL for the ES-module import of `dotnet.js` (Node must use `file://`).
- `resolveReferencePack?()` - optional descriptor for serving `@carbide/refs-net10.0` over HTTP.

### Node

In Node, the Node-only adapter export lives under the subpath:

```ts
import { NodeHostAdapter } from "@carbide/core/node";
```

`NodeHostAdapter` defaults to `assetDelivery: "http"` (it spins up a localhost server) because
Mono-WASM's `file://` fetch path is still unreliable for this workload.

### Browser

In the browser, you generally must serve `_framework/` as static assets. One working shape is
to host the installed package root and point the adapter at the `_framework/` directory:

```ts
import { BrowserHostAdapter, CarbideSession } from "@carbide/core";

const adapter = new BrowserHostAdapter({
    frameworkAssetsBaseUrl: new URL(
        "/node_modules/@carbide/core/src/bin/Release/net10.0/publish/wwwroot/_framework/",
        window.location.origin,
    ).toString(),
});
const session = await CarbideSession.initializeAsync({ hostAdapter: adapter });
```

See `test/browser/*.html` in this package for minimal browser-host examples.

## Build from source (maintainers)

Prerequisites: .NET 10 SDK with the `wasm-tools` workload and Node.js 20+.

```bash
dotnet publish -c Release src/Carbide.Core.csproj
npm install
npm run build:ts
npm test
```

## Origin

Carbide starts as a structural fork of WasmSharp. See [`ATTRIBUTION.md`](./ATTRIBUTION.md) for
the list of files adapted from upstream sources.
