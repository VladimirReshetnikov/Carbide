# @carbide/cli

Command-line interface for Carbide: compile, run, and validate C# projects without a .NET SDK install. Wraps `@carbide/core` with three scripted subcommands.

## Install

```bash
npm install -g @carbide/cli            # system-wide `carbide` binary
# or invoke without installing:
npx @carbide/cli --help
```

`@carbide/refs-net10.0` is recommended alongside so the compile-time API surface is stable:

```bash
npm install -g @carbide/cli @carbide/refs-net10.0
```

## Commands

### `carbide build`

Compile one or more `.cs` files into a `.dll` (+ portable `.pdb`) without executing.

```bash
carbide build --source Thing.cs --assembly-name MyLib --out out/lib/
```

| Flag | Description |
|---|---|
| `--project <path>.csproj` | Read a `.csproj` and build per its options. Mutually exclusive with `--source` / `--assembly-name` / `--target-framework`. Since M5. |
| `--source <path>` | Source file. Repeatable. `-` reads one source from stdin. |
| `--ref <path>` | Reference DLL. Repeatable. Bytes are passed via `session.addReference`. |
| `--out <dir>` | Output directory. Writes `<assembly-name>.dll` and `<assembly-name>.pdb`. Pass `-` to write PE bytes to stdout (no PDB). |
| `--assembly-name <n>` | Assembly name. Default: basename of first source. |
| `--target-framework <t>` | Currently informational; `net10.0` is the only supported TFM. |
| `--no-debug` | Skip writing the `.pdb`. |
| `--format json\|human` | Output format. Default `json`. |

Exit codes: `0` success, `1` compile errors, `2` I/O / unexpected error, `3` unsupported flag combination.

Since M5, Carbide's Roslyn compiler runs with `Deterministic=true`, so two invocations with the same inputs produce byte-identical PE. A `carbide build --project Foo.csproj` produces the same bytes as `carbide build --source <files>` with the equivalent options flattened.

### `carbide run`

Compile and execute the program. The program's stdout/stderr stream through to the outer process under `--format human`.

Under `--format json` (default) stdout/stderr are captured into a JSON trailer on stdout. Consumers should parse the **last non-empty stdout line** as JSON: user code can bypass Carbide's current capture by writing directly to stdout via `Console.OpenStandardOutput()` (or similar), which may prefix raw bytes before the trailer.

The CLI parser accepts `-- <program args>...`, but program arguments are not forwarded into the runtime yet (the user program currently sees an empty `string[] args`).

```bash
carbide run --source Program.cs --ref out/lib/MyLib.dll --format human
```

### `carbide validate`

Run Roslyn diagnostics only; no emit, no execution. Useful for CI linters and pre-commit hooks.

```bash
carbide validate --source Program.cs --format json
```

Exit `0` when no error-severity diagnostics, non-zero otherwise.

## Round-trip example

Build a library, reference its emitted DLL from a second project, run it:

```bash
# Stage 1: build MyLib.dll.
carbide build --source Thing.cs --assembly-name MyLib --out out/lib/

# Stage 2: reference MyLib.dll from Program.cs, run.
carbide run --source Program.cs --ref out/lib/MyLib.dll --format human
# → Thing<42>
```

## Project-file example (M5)

```bash
# Foo.csproj + sources on disk → compiled DLL.
carbide build --project Foo.csproj --out out/
carbide run --project Foo.csproj
carbide validate --project Foo.csproj
```

See [`@carbide/msbuild-lite`](../msbuild-lite/README.md) for the supported `.csproj` subset. Current behavior is:

- `<PackageReference>` is parsed by `@carbide/msbuild-lite` and then resolved by `@carbide/nuget` when the CLI runs in `--project` mode.
- `<ProjectReference>` triggers a graph walk: every sibling `.csproj` reachable via `<ProjectReference>` edges is parsed, compiled in topological order (leaves first), and its PE is attached as a metadata reference for downstream consumers. Each sub-project keeps its own `carbide.lock.json`.

### Sibling project builds (M9)

```
App/App.csproj               # root, references ../Lib/Lib.csproj
Lib/Lib.csproj               # leaf library
```

```bash
carbide build --project App/App.csproj --out out/
# → out/App.dll, out/App.pdb, out/MyLib.dll, out/MyLib.pdb

carbide run --project App/App.csproj
# → hello world
```

Cycles are a hard error (`MSPROJ001`, exit 1). AssemblyName collisions across sub-projects are a hard error (`MSPROJ002`, exit 3). `--out -` (PE bytes to stdout) is rejected for multi-project graphs (`MSPROJ003`, exit 3). Missing `<ProjectReference>` targets surface as `MSPROJ004` (exit 1). Under `--format json`, each diagnostic carries a `project` field naming the csproj the error originated in (null for the root, so single-project output is byte-identical). `.sln` parsing is not in scope; the CLI's `--project` flag takes one `.csproj`.

When a project declares packages, Carbide writes `carbide.lock.json` next to the project by default and replays it on subsequent runs:

```bash
carbide run --project Foo.csproj
carbide run --project Foo.csproj --offline
```

Use `--allow-list-mode advisory` or `--allow-list-mode off` if you need to experiment outside Carbide's default allow-list policy. See [`@carbide/nuget`](../nuget/README.md) for resolver details and warning codes.

## Exit-code summary

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Compile errors reported under `diagnostics`. |
| 2 | I/O or unexpected internal error. |
| 3 | Unsupported or malformed CLI flag combination. |
| *N* | For `carbide run`, the user program's own exit code. |

## Performance note

Each `carbide` invocation boots the embedded .NET WASM runtime once. Cold start is measured in seconds on first use; subsequent invocations from a warm Node process start faster but each CLI run is a fresh subprocess. Long-lived consumers should prefer the `@carbide/core` API directly over spawning `carbide` in a loop.
