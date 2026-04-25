# Proposal: `cscript.exe` as a VFS-backed Windows Script Host stub

- Created (UTC): 2026-04-25T01:27:24Z
- Repository HEAD: ed607a3896a4f287c06867e37b5c0bde06471870
- Status: Draft
- Audience: Vladimir; Carbide shell maintainers; future agents adding Windows automation compatibility
- Scope: Adding `cscript.exe` to the Carbide virtual executable catalog, defining its shell-implemented runtime contract, and bounding the Windows Script Host behavior that should run inside the shared VFS/session model
- Related code:
  - `src/Carbide/packages/carbide-multishell/src/VirtualExecutableCatalog.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Core.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Basic.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Advanced.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/VirtualExecutable.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Apps/StubInstaller.cs`
  - `src/Carbide/packages/carbide-multishell-tests/VirtualExecutableTests.cs`
- Related docs:
  - [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md)
  - [Detailed implementation plan: `carbide-multishell` virtual executable stubs](../planning/carbide-multishell-vfs-executable-stubs-detailed-plan__2026-04-23__01-18-24-060735__6d4f2a9b1c7e.md)
  - [PowerShell-subset shell for Carbide + xterm.js](carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
- External references:
  - [Microsoft Learn: `cscript`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cscript)
  - [Microsoft Learn: `wscript`](https://learn.microsoft.com/en-au/windows-server/administration/windows-commands/wscript)
  - [Microsoft Learn: Using COM Objects in Windows Script Host](https://learn.microsoft.com/en-us/windows/win32/com/using-com-objects-in-windows-script-host)

## Summary

Carbide already exposes a path-based virtual executable catalog for common `System32` and Git-style tools. `cscript.exe` should join that catalog as a Windows-personality stub installed at `/Windows/System32/cscript.exe`, but it must not spawn the host machine's Windows Script Host. Its behavior should be implemented entirely inside the Carbide shell runtime against the shared VFS, shared environment store, and shared dispatcher.

This is materially different from adding another filter command like `findstr.exe`. `cscript.exe` is a script host. The stub is only the entrypoint; the real feature is a bounded Windows Script Host runtime that can parse host options, load `.js`, `.vbs`, and later `.wsf` scripts from the VFS, expose a safe WSH object model, and route script I/O through the active shell invocation. The proposal recommends a staged implementation: first land the catalog entry and command-line host contract, then add a small WSH runtime with a practical subset of JScript/VBScript automation semantics, and keep COM, registry, network, UI, and real-host process access outside the trust boundary.

## Current state

The implemented virtual executable system already has the right structural seam:

- `VirtualExecutableCatalog` defines command ids, personalities, stub paths, search names, and a handler key.
- `StubInstaller.Install(...)` materializes VFS files such as `/Windows/System32/findstr.exe` and registers the corresponding virtual executable definition with `ShellDispatcher`.
- `ShellDispatcher.Resolve(...)` supports exact VFS paths, drive-qualified Windows spellings, bare command names, shell-specific default search roots, and implicit extensions.
- `MultishellVirtualExecutableHandler.Execute(...)` dispatches by `CommandId` to concrete C# handlers.
- `carbide-pwsh` includes the shared multishell catalog and handler sources when built as the single public endpoint.

`cscript.exe` is not currently in the catalog. A call such as `cscript //nologo C:\work\hello.js` therefore fails before any script-host semantics can be considered. Adding the stub to the catalog is mechanically small, but implementing useful behavior is a larger compatibility decision because WSH scripts expect a host object model, script engines, argument collections, standard streams, exit-code behavior, and often automation objects such as `Scripting.FileSystemObject`.

## Why `cscript.exe` belongs in the catalog

Windows automation scripts frequently use `cscript.exe` as a console-friendly script host from batch files, PowerShell, and installer-style command lines. Microsoft's command reference defines it as the command-line host for script files and documents host options such as `/e:<engine>`, `/job:<identifier>`, `/logo`, `/nologo`, `/t:<seconds>`, and `/u`. The same reference calls out `.vbs`, `.js`, and `.wsf` as usual Windows Script Host file types.

For Carbide, this matters because many realistic Windows command recipes do not stop at `cmd`, `findstr`, and `robocopy`. They often include small WSH snippets for text processing, filesystem automation, and glue logic. A VFS-backed `cscript.exe` gives those scripts a familiar entrypoint while preserving Carbide's core invariant: all observable work stays inside the browser/Mono-WASM shell runtime and its virtualized state.

The feature is especially valuable for:

- cmd scripts that invoke `cscript //nologo helper.vbs ...`;
- pwsh scripts that call old Windows automation helpers rather than rewriting them;
- smoke tests for mixed Windows shell compatibility;
- migration demos that show legacy Windows scripting patterns running inside the VFS sandbox.

## Scope and non-goals

### In scope

- Add a Windows-personality virtual executable definition for `cscript.exe`.
- Materialize `/Windows/System32/cscript.exe` through the existing stub installer.
- Resolve `cscript` from cmd and pwsh through existing PATHEXT behavior.
- Make `Get-Command cscript` and `where cscript` report the virtual executable.
- Implement a shell-owned `windows-cscript` handler, not a host-process bridge.
- Parse the documented `cscript` host options that are meaningful in a console shell.
- Load script files from the shared VFS and pass script arguments through to the script runtime.
- Expose a bounded WSH object model over VFS, env, stdin/stdout/stderr, and dispatcher-backed command execution.
- Support a practical first subset of `.js` and `.vbs` scripts, with `.wsf` parsing as a later extension point.

### Out of scope

- Spawning real `C:\Windows\System32\cscript.exe`.
- Using real COM, ActiveX, Windows registry, Task Scheduler, WMI, services, network sockets, or the real host filesystem.
- Implementing `wscript.exe` GUI/dialog behavior in the first change.
- Implementing encoded scripts such as `.jse` or `.vbe`.
- Full JScript, VBScript, or WSF parity.
- Browser DOM, Node.js, or modern JavaScript semantics for `.js` files.
- A generic plug-in model for arbitrary ActiveX scripting engines.
- Persistent per-user WSH defaults for `/s`, `/h:cscript`, or `/h:wscript`.

## Proposed catalog change

Add one Windows-personality definition:

```csharp
yield return Windows("windows-cscript", "cscript.exe");
```

The installed stub path should be:

```text
/Windows/System32/cscript.exe
```

No extensionless `/Windows/System32/cscript` file is required. Current shell-core resolution already tries `.com`, `.exe`, `.cmd`, and `.bat` for cmd and pwsh when the caller omits an extension. That means:

- `cmd` can resolve `cscript`;
- `pwsh` can resolve `cscript`;
- `bash` can resolve `cscript.exe` when `/Windows/System32` is in the search roots, and can also resolve `cscript` through bash's `.exe` compatibility suffix behavior.

This proposal intentionally does not add `/usr/bin/cscript.exe` or `/bin/cscript.exe`. `cscript.exe` is a Windows personality tool. If a bash script needs it, it should find the Windows root through PATH or use an explicit path. That keeps the catalog's path/personality rule consistent with `find.exe`, `sort.exe`, and `tar.exe`.

## Runtime architecture

### Handler placement

The implementation should add a new handler branch:

```csharp
"windows-cscript" => ExecuteWindowsCscript(invocation),
```

The handler code should live in either:

- a new partial file `MultishellVirtualExecutableHandler.Wsh.cs`, if the WSH runtime is nontrivial; or
- `MultishellVirtualExecutableHandler.Advanced.cs`, if the first slice is intentionally small.

The separate partial file is preferable. `cscript.exe` is not just another command-line filter; it will accumulate host option parsing, script-engine routing, WSH object model code, and compatibility tests. Keeping that surface isolated will reduce pressure on the already-large multishell handler files.

### Core components

The proposed internal model is:

- `WindowsScriptHostCommand`
  - Parses `cscript` host options and script path.
  - Loads script text from VFS.
  - Selects a script engine.
  - Owns logo/help/error output shape.
- `WshHostOptions`
  - `BatchMode`
  - `InteractiveMode`
  - `EngineName`
  - `JobName`
  - `Logo`
  - `NoLogo`
  - `TimeoutSeconds`
  - `UnicodeRedirectedIo`
  - `DebuggerRequested`
  - `PersistDefaultsRequested`
- `WshExecutionContext`
  - Wraps `VirtualExecutableInvocation`.
  - Exposes `ScriptFullName`, `ScriptName`, `ScriptDirectory`, args, stdin, stdout, stderr, VFS, env, and dispatcher.
  - Tracks `ExitCode`.
  - Carries a cancellation/timeout token when the engine can observe one.
- `IWshScriptEngine`
  - Executes one script text payload under a `WshExecutionContext`.
  - Returns a WSH-style exit code and diagnostic information.
- `WshObjectModel`
  - Implements the safe subset of `WScript`, `WScript.Arguments`, text streams, `Scripting.FileSystemObject`, and a bounded `WScript.Shell`.

The handler should not call into cmd, pwsh, or bash interpreters to execute the script itself. Re-entering a shell would make `.vbs`/`.js` behavior depend on whichever shell happened to invoke `cscript`, which is the wrong ownership model. Shells own command dispatch. The `cscript.exe` handler owns WSH semantics.

## Command-line contract

### Option spelling

The handler should accept both single-slash and double-slash host options:

```text
cscript /nologo hello.js
cscript //nologo hello.js
```

Microsoft Learn currently documents single-slash option spellings, while common Windows usage and real local behavior also accepts double-slash forms such as `//nologo`. Accepting both is low-risk and avoids surprising users who learned WSH through historical examples.

Options are case-insensitive.

### Host options

Recommended first behavior:

| Option | Proposed behavior |
|---|---|
| `/?`, `//?`, no script path | Print cscript-style help or a concise compatibility help message; return nonzero for no-script input, matching local Windows behavior observed during this proposal. |
| `/logo`, `//logo` | Print a synthetic Windows Script Host banner before executing the script. This is the default unless `/nologo` appears. |
| `/nologo`, `//nologo` | Suppress the banner. |
| `/b`, `//b` | Batch mode. Suppress interactive prompts and treat unsupported prompts as errors. |
| `/i`, `//i` | Interactive mode. This is the default. Prompting can be supported later through stdin/stdout; message boxes remain unsupported. |
| `/e:<engine>`, `//e:<engine>` | Select a registered engine by name, such as `jscript`, `javascript`, or `vbscript`. |
| `/job:<identifier>`, `//job:<identifier>` | Parsed and stored. Return a clear unsupported error until `.wsf` job execution is implemented. |
| `/t:<seconds>`, `//t:<seconds>` | Enforce a best-effort timeout for cooperative script engines. Reject values outside the documented 0..32767-ish range with a host-option error. |
| `/u`, `//u` | Accepted as a compatibility flag. Initially a no-op because Carbide virtual executable streams are text streams rather than byte streams with console redirection encoding. |
| `/d`, `/x`, `//d`, `//x` | Accepted but return a clear unsupported-debugger error unless the script can still run safely without a debugger. The first implementation should prefer explicit unsupported behavior. |
| `/h:cscript`, `/h:wscript`, `//h:cscript`, `//h:wscript`, `/s`, `//s` | Accepted but do not persist global defaults. Return success only if no script execution is requested, or emit a warning that persistence is not modeled. |

### Script path and script arguments

Host options are parsed before the script path. After the first non-option token that resolves as the script path, all remaining tokens are script arguments, including tokens beginning with `/` or `//`.

This rule is important because WSH script arguments often look like host options. For example:

```text
cscript //nologo helper.vbs /mode:copy /verbose
```

Here `/mode:copy` and `/verbose` must be script arguments, not cscript host options.

The handler should pass script arguments through in original order. It should expose them through the WSH arguments collection and should not reinterpret quoting after the shell has already tokenized the command.

## Script engine selection

Engine selection should follow this order:

1. Explicit `/e:<engine>` option.
2. Script extension:
   - `.js` maps to `jscript`.
   - `.vbs` maps to `vbscript`.
   - `.wsf` maps to `wsf`.
3. If neither an engine nor a recognized extension is available, fail with a WSH-style "no script engine" error.

The first implementation should not try to associate arbitrary extensions globally. If a user wants to run `helper.admin` as VBScript, they should pass `/e:vbscript helper.admin`, and that association should last only for that invocation.

## Recommended language support

### First useful slice

The first useful slice should support:

- `.js` scripts with a bounded WSH JScript subset sufficient for command-line glue:
  - `var` declarations;
  - assignments;
  - numeric/string/boolean/null values;
  - property access and method calls;
  - `if`, `while`, `for`, `break`, `continue`;
  - function declarations;
  - arrays and simple objects;
  - `try`/`catch` only if the implementation can do it without distorting error semantics;
  - `WScript.Echo`, `WScript.Quit`, `WScript.Sleep`, `WScript.Arguments`, `WScript.StdIn`, `WScript.StdOut`, `WScript.StdErr`;
  - `Scripting.FileSystemObject` and selected `WScript.Shell` operations through `WScript.CreateObject`.
- `.vbs` scripts with a bounded automation subset:
  - `Dim`, `Set`, `Const`;
  - assignments;
  - `If ... Then ... Else ... End If`;
  - `For ... Next`, `For Each ... Next`, `Do ... Loop`;
  - `Sub` and `Function`;
  - `On Error Resume Next` only if the error object model is implemented enough to be honest;
  - `WScript` object calls;
  - basic string and numeric built-ins used in small automation scripts.

This is a significant mini-language surface. If we choose to reduce the first implementation, prefer reducing the language breadth before reducing the host contract. A perfect option parser with a small engine is easier to expand safely than a fuzzy option parser that runs a larger but surprising language subset.

### Why not use the browser or host JavaScript engine?

WSH JScript is not browser JavaScript and not Node.js. It has WSH globals, ActiveX-style object creation, different host I/O expectations, and legacy language assumptions. Running `.js` through the page's JavaScript engine would cross trust boundaries, expose the host/page environment, and produce semantics that are wrong for Windows automation scripts.

The script engine should be managed code running inside the same shell runtime as the rest of the virtual executable catalog. If a third-party managed interpreter is considered later, it must work in the browser build, have an acceptable payload cost, and allow us to sandbox host objects. A clean-room bounded interpreter remains the most predictable first option.

## WSH object model subset

### `WScript`

The first implementation should expose:

| Member | Proposed behavior |
|---|---|
| `WScript.Echo(...)` | Writes arguments joined by spaces, followed by a newline, to `invocation.Output`. |
| `WScript.Quit([code])` | Stops script execution and returns the specified integer exit code, defaulting to 0. |
| `WScript.Sleep(milliseconds)` | Cooperative delay. Must not block the browser event loop in a way that prevents cancellation or terminal progress. |
| `WScript.Arguments` | Ordered collection of script args. Include `Count`, index access, and simple named/unnamed partitions if practical. |
| `WScript.StdIn` | Read-only text stream backed by `invocation.Input`. |
| `WScript.StdOut` | Write-only text stream backed by `invocation.Output`. |
| `WScript.StdErr` | Write-only text stream backed by `invocation.Error`. |
| `WScript.ScriptName` | Leaf name of the script path. |
| `WScript.ScriptFullName` | VFS absolute path of the script. |
| `WScript.FullName` | VFS path of the cscript stub, normally `/Windows/System32/cscript.exe`. |
| `WScript.Path` | VFS directory containing `cscript.exe`, normally `/Windows/System32`. |
| `WScript.CreateObject(progId)` | Creates only supported fake automation objects. Unsupported progids fail with a WSH-style automation error. |

### Text streams

Text streams should be line-oriented over `TextReader` and `TextWriter`:

- `ReadLine()`
- `Read(count)`
- `ReadAll()`
- `Write(value)`
- `WriteLine(value)`
- `WriteBlankLines(count)`
- `AtEndOfStream`

The implementation should not expose byte-level encoding semantics initially. `/u` can be accepted as a compatibility flag without changing the stream shape.

### `Scripting.FileSystemObject`

This is the highest-value fake automation object. It should operate only against `VirtualFileSystem`.

Recommended first members:

- `FileExists(path)`
- `FolderExists(path)`
- `CreateFolder(path)`
- `DeleteFile(path, force)`
- `DeleteFolder(path, force)`
- `CopyFile(source, destination, overwrite)`
- `CopyFolder(source, destination, overwrite)`
- `MoveFile(source, destination)`
- `MoveFolder(source, destination)`
- `OpenTextFile(path, mode, create, format)`
- `CreateTextFile(path, overwrite, unicode)`
- `GetFileName(path)`
- `GetBaseName(path)`
- `GetExtensionName(path)`
- `GetParentFolderName(path)`
- `BuildPath(parent, child)`
- `GetAbsolutePathName(path)`

The object should preserve Carbide path normalization rules. Drive-qualified paths such as `C:\work\foo.txt` should normalize through existing VFS path handling rather than reaching the host filesystem.

### `WScript.Shell`

`WScript.Shell` should be intentionally smaller:

- `Environment(scope)` returns a view over `EnvVarStore`.
- `ExpandEnvironmentStrings(text)` expands `%NAME%` from `EnvVarStore`.
- `Run(command, windowStyle, waitOnReturn)` can dispatch through the shared `ShellDispatcher` when `waitOnReturn` is true or omitted.
- `Exec(command)` should remain deferred unless we introduce a process-like async virtual execution object with stdout/stderr streams.

Do not implement registry methods (`RegRead`, `RegWrite`, `RegDelete`) in the first version. Carbide has no registry model in this subsystem, and silently treating registry calls as environment variables would create bad compatibility fiction.

## Exit codes and error behavior

Exit-code rules:

- Normal script completion returns 0.
- `WScript.Quit(n)` returns `n`.
- Host option errors return 1.
- Missing script file returns 1.
- Unsupported engine or unsupported WSF job returns 1.
- Runtime script errors return 1 unless a supported language feature explicitly handles the error.

Output rules:

- `/nologo` suppresses the banner.
- `/logo` or default mode prints a synthetic WSH banner. The exact Windows copyright string is not important and should not be copied.
- Host diagnostics go to stderr.
- Script output through `WScript.Echo` and `StdOut` goes to stdout.
- Script output through `StdErr` goes to stderr.

For pwsh invocation, existing virtual executable pipeline bridging should apply. That means stdout re-enters the pwsh pipeline as strings, `$LASTEXITCODE` receives the handler exit code, and `$?` reflects success only for exit code 0.

## `.wsf` support

`.wsf` files should be explicitly staged after `.js` and `.vbs`.

The first `.wsf` implementation should:

- parse XML from VFS;
- support `<job id="...">`;
- honor `/job:<identifier>`;
- execute `<script language="JScript">` and `<script language="VBScript">` blocks using the same bounded engines;
- reject unsupported XML elements and unsupported language names clearly.

Do not implement multi-engine shared global state until it is needed by a test fixture. WSF's multi-engine model is a deep compatibility surface, and a single-engine job subset will already support useful scripts.

## Security and trust boundaries

The trust boundary is the shared Carbide session, not the host operating system.

The `cscript.exe` handler must not:

- open host filesystem paths outside the VFS;
- create real COM objects;
- read or write the host registry;
- spawn host processes;
- access the network;
- use browser globals as script globals;
- persist per-user WSH defaults outside the session.

It may:

- read and write VFS files;
- read and mutate `EnvVarStore` through supported WSH objects;
- call back into `ShellDispatcher` for `WScript.Shell.Run`;
- use the active invocation's stdin/stdout/stderr;
- observe synthetic identity values already present in the session (`USER`, `USERNAME`, `HOSTNAME`, `HOME`).

Unsupported host capabilities should fail loudly. Returning fake success for registry, COM, WMI, or network automation would make script results untrustworthy.

## Interaction with command discovery and completion

Adding the catalog entry should make the tool visible through existing surfaces:

- `Get-Command cscript` in pwsh should return an `Application` result pointing at `/Windows/System32/cscript.exe`.
- `where cscript` in cmd should include a Windows path spelling for the stub.
- pwsh prompt completion should include `cscript` and `cscript.exe` through `GetInteractiveCommandNames()`.

No pwsh cmdlet should be added. `cscript.exe` is a native-looking virtual executable, not a PowerShell command.

## Testing requirements

### Catalog and resolution tests

- `RepresentativeStubCatalogIsInstalledAcrossRoots` should assert `/Windows/System32/cscript.exe`.
- Dispatcher tests should verify:
  - `Resolve("cscript", ..., "cmd")` returns `/Windows/System32/cscript.exe`;
  - `Resolve("cscript", ..., "pwsh")` returns `/Windows/System32/cscript.exe`;
  - `Resolve(@"C:\Windows\System32\cscript", ..., "cmd")` returns `/Windows/System32/cscript.exe`;
  - `Resolve("cscript.exe", ..., "bash")` can find the Windows root under default bash fallback search roots.

### Command-line host tests

- `cscript` with no script reports a host input error and returns 1.
- `cscript /?` and `cscript //?` print help text.
- `/nologo` suppresses the banner.
- `/logo` prints the synthetic banner.
- Host options are case-insensitive.
- Host option parsing stops at the script path.
- Script arguments beginning with `/` are passed through after the script path.
- `/e:jscript` and `/e:vbscript` select engines for nonstandard extensions.
- Unsupported `/job` on a `.js` or `.vbs` file fails clearly.

### Script runtime tests

- `.js`: `WScript.Echo("hello")` writes `hello`.
- `.js`: `WScript.Quit(7)` returns exit code 7.
- `.js`: `WScript.Arguments(0)` sees the first script argument.
- `.js`: `WScript.StdIn.ReadLine()` consumes piped input.
- `.vbs`: `WScript.Echo "hello"` writes `hello`.
- `.vbs`: `WScript.Quit 7` returns exit code 7.
- `Scripting.FileSystemObject` can create, read, copy, and delete VFS text files.
- `WScript.Shell.Environment("Process")("FOO") = "bar"` updates `EnvVarStore` if that setter is included.
- `WScript.Shell.Run "grep beta C:\work\data.txt"` dispatches through the virtual executable catalog and returns the child exit code.

### Negative tests

- Real host paths are not accessible.
- Unsupported `CreateObject("WbemScripting.SWbemLocator")` fails.
- Unsupported `CreateObject("MSXML2.XMLHTTP")` fails.
- Unsupported registry operations fail.
- Unsupported script extensions fail unless `/e:<engine>` selects a known engine.
- A timeout on a cooperative loop returns a timeout error rather than hanging the browser session.

## Implementation plan

### Workstream A: catalog and handler shell

Deliverables:

- Add `windows-cscript` to `VirtualExecutableCatalog`.
- Add `ExecuteWindowsCscript(...)` dispatch branch.
- Add help/no-script/logo/nologo option parser.
- Add catalog and basic command-line tests.
- Add `PromptParameterCatalog` entries only if prompt completion later exposes external executable option completion. No such change is required by this proposal.

This workstream is small in code surface: one catalog entry, one dispatch branch, one handler skeleton, and several tests.

### Workstream B: WSH host object model

Deliverables:

- `WshExecutionContext`
- `WScript` object
- arguments collection
- standard text streams
- safe fake `Scripting.FileSystemObject`
- bounded fake `WScript.Shell`

This workstream is the foundation for both `.js` and `.vbs`. It should land before either language engine grows beyond toy examples.

### Workstream C: first script engine

Deliverables:

- A bounded `.js` or `.vbs` engine sufficient for smoke tests.
- Engine selection by extension and `/e:<engine>`.
- Runtime error reporting.
- `WScript.Quit` control flow.

The recommended first engine is `.js` because a small expression/statement interpreter can be grown incrementally, and local real-Windows probes are easy to write. The recommended second engine is `.vbs`, because legacy Windows automation value is much higher once VBScript works.

### Workstream D: VBScript and `.wsf`

Deliverables:

- Practical VBScript automation subset.
- `.wsf` XML parsing and `/job:<identifier>`.
- Shared WSH runtime tests for `.js`, `.vbs`, and `.wsf` where semantics overlap.

This workstream should not be bundled with the first catalog change unless the implementation already has a credible VBScript parser ready.

## Risks and edge cases

| Risk | Why it matters | Mitigation |
|---|---|---|
| `cscript.exe` looks small but implies script engines | The executable is a host, not the actual language runtime | Stage the work and keep host parsing separate from engine execution |
| WSH JScript is not modern JavaScript | Browser or Node semantics would be wrong and unsafe | Implement a managed bounded engine or choose a managed interpreter only after payload and sandbox review |
| VBScript is a separate language project | Legacy value is high, but parser/runtime size is nontrivial | Define an explicit automation subset and grow it from fixtures |
| Real COM is impossible inside the VFS sandbox | Many WSH scripts call `CreateObject` | Provide a small fake automation object registry and fail unsupported progids loudly |
| `/t` timeout cannot preempt arbitrary tight loops unless engines cooperate | Browser sessions must not hang | Instrument interpreter loops and document that timeout is cooperative |
| `/u` is byte-encoding oriented but Carbide virtual executables use text streams | Implementing it literally would require stream/encoding plumbing outside current handler shape | Accept it as a compatibility flag initially and document it as no-op |
| `WScript.Shell.Run` can recursively dispatch commands | Recursive shell execution can produce confusing errors or accidental infinite loops | Route only through `ShellDispatcher`, preserve exit codes, and rely on existing command resolution; add recursion-sensitive tests |
| WSF multi-engine jobs can balloon scope | WSF supports multiple engines and jobs | Start with single-job, single-engine blocks and explicit unsupported errors |

## Recommendation

Add `cscript.exe` to the Windows virtual executable catalog, but treat the catalog entry as only the first artifact of a larger WSH compatibility slice. The correct architecture is a shell-owned `windows-cscript` handler that parses cscript host options, loads scripts from the VFS, and executes them through a bounded managed WSH runtime.

The first accepted implementation should include:

- `/Windows/System32/cscript.exe` stub installation;
- command discovery and resolution through existing virtual executable machinery;
- host option parsing for the documented cscript flags;
- help/no-script/logo/nologo behavior;
- script path and script argument handling;
- one small language-engine slice proving `WScript.Echo`, `WScript.Quit`, and `WScript.Arguments`;
- tests that prove no host filesystem or real COM access is possible.

After that, expand by adding the WSH object model and VBScript/WSF support from fixtures. That path keeps Carbide honest: legacy Windows scripts become more compatible over time, but all behavior remains implemented in the shell and confined to the VFS-backed session.
