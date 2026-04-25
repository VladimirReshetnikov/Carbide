# carbide-pwsh browser dotnet interactive test report

- Created (UTC): 2026-04-25T22:33:27Z
- Repository HEAD: 020144bd373ad7d9aa29914e84c14963d18d87f4
- Document type: implementation and validation report
- Scope: `src/Carbide/packages/carbide-pwsh` browser endpoint and `dotnet` VFS facade

## Summary

I implemented a browser-level interactive test harness for `carbide-pwsh` and used it to
exercise the new `dotnet` VFS facade from the same public page a human user opens. The
result is a reusable Node/Playwright harness plus two non-trivial `dotnet` scenarios that
type or paste commands into xterm, create C# files in the VFS, compile them, run the
resulting assemblies, and assert on the terminal transcript and `$LASTEXITCODE`.

The first attempt exposed a real interactive prompt issue: pasted multi-character chunks
only delivered their first character to the C# prompt editor. I fixed that in the core
interactive terminal path by preserving unconsumed raw bytes between `ReadKeyAsync` calls.
After republishing `@carbide/core`, both browser `dotnet` scenarios passed.

## Test Infrastructure Created

The new infrastructure consists of:

- Reusable static-server exports in `packages/carbide-pwsh/scripts/serve.mjs`.
- `packages/carbide-pwsh/test/browser/pwsh-browser-harness.mjs`, which starts the real
  browser endpoint, launches Chromium, waits for the xterm-hosted pwsh prompt, sends
  keyboard or paste input, reads terminal text, and records failure artifacts.
- `packages/carbide-pwsh/test/browser/dotnet-interactive.test.mjs`, which contains the
  first browser-level `dotnet` scenarios.
- `packages/carbide-pwsh/package.json` scripts `test:browser` and
  `test:browser:dotnet`.

The companion current-state documentation is:

- `src/Carbide/docs/carbide-pwsh-browser-interactive-test-infrastructure__2026-04-25__22-33-27__a819b7f46f3d.md`

That file should be read as the operational contract for adding future tests.

## Scenarios Exercised

### Loose-source build plus direct DLL run

The first scenario opens the real `carbide-pwsh` page, then enters commands through xterm:

- Creates `/work`.
- Pastes `Set-Content` commands that write `/work/Calc.cs` and `/work/Program.cs`.
- Runs `dotnet build /work/Program.cs /work/Calc.cs -o /work/out`.
- Verifies the transcript contains `built /work/out/Program.dll`.
- Runs `Test-Path /work/out/Program.dll`.
- Runs `dotnet /work/out/Program.dll 7 11 13`.
- Verifies the app prints `sum=31` and `7|11|13`.
- Verifies `$LASTEXITCODE` is `1`, the app's return value.

This covers loose multi-source compilation, VFS artifact writes, direct executable assembly
loading, argument passing, stdout propagation, and external-command exit code propagation.

### Project-file run with default source discovery

The second scenario creates a small VFS project:

- Creates `/project/PolyDemo.csproj` with `OutputType`, `TargetFramework`,
  `ImplicitUsings`, `Nullable`, and `AssemblyName`.
- Creates `Formatter.cs` and `Program.cs`.
- Runs `dotnet run --project /project/PolyDemo.csproj -- alpha beta gamma`.
- Verifies the app prints a SHA256-derived digest prefix.
- Verifies it prints `0:ALPHA;1:BETA;2:GAMMA`.
- Verifies `$LASTEXITCODE` is `3`, the app's return value.
- Verifies `Get-Command dotnet` discovers `dotnet` as an `Application`.

This covers project-file dispatch, default `.cs` source discovery under the project
directory, browser-side Carbide compilation, program argument forwarding, nonzero
application exit code propagation, and shell command discovery for the VFS stub.

## Issue Found And Fixed

### Pasted xterm chunks dropped all but the first key

Initial failure transcripts showed:

```text
PS /home/user> S
The term 'S' is not recognized as a cmdlet, function, or script.
PS /home/user> dotnet build /work/Program.cs /work/Calc.cs -o /work/out
dotnet: source '/work/Program.cs' was not found in the VFS.
```

The harness was using xterm's paste path for long `Set-Content` commands. The transcript
showed that only the first `S` from `Set-Content ...` reached the prompt. That meant no C#
source files were created, and `dotnet build` correctly reported missing VFS files.

The root cause was in `CarbideConsole.ReadKeyAsync`. In browser prompt-editing mode,
xterm paste arrives as a multi-character raw chunk. `ReadKeyAsync` drained that whole
chunk from `BrowserTerminalReader`, parsed one `ConsoleKeyInfo`, returned it, and lost the
unconsumed tail because the local parse buffer was scoped to one call.

The fix adds `BrowserTerminalReader.PrependRaw(...)` and returns unconsumed parse-buffer
bytes to the front of the raw input buffer before `ReadKeyAsync` returns. Subsequent
`ReadKeyAsync` calls now see the remaining pasted characters in order.

This is a real human-facing improvement: pasting commands into the browser prompt now
behaves like typing them character by character.

## Issues Still Present Or Intentionally Out Of Scope

- Each scenario launches a fresh browser and recompiles the shell. That maximizes
  isolation and keeps the page path realistic, but it is heavier than a shared fixture.
- The harness currently asserts from xterm transcripts. It does not yet expose structured
  shell state snapshots, which is intentional for black-box coverage but less convenient
  when diagnosing semantic state that is not printed.
- `dotnet restore` remains intentionally bounded. Projects with `PackageReference` still
  report that package restore is not wired into the browser facade.
- The tests use xterm's programmatic paste method for long commands. That is the right
  terminal-path coverage for paste chunks, but it does not validate browser-native
  clipboard permission behavior.
- Node's built-in test runner does not stream per-command progress by default. On a hard
  hang, the artifact files are the best evidence, but they are written only after the test
  catches a failure rather than during the hang.

## Validation Commands

The following commands were run after implementing the harness and fixing paste chunk
handling:

```bash
cd src/Carbide/packages/core
npm run build:dotnet

cd ../carbide-pwsh
node --test --test-force-exit --test-name-pattern "loose-source" test/browser/dotnet-interactive.test.mjs
node --test --test-force-exit --test-name-pattern "run --project" test/browser/dotnet-interactive.test.mjs
npm run test:browser:dotnet
```

Final result:

```text
npm run test:browser:dotnet
pass 2
fail 0
```

The `npm run build:dotnet` command emitted the existing analyzer warning stream, but no
build errors.
