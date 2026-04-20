# Carbide code review report

I reviewed the code under the extracted `Carbide/` tree you attached (the monorepo slice rooted at `:/src/Carbide`). Concretely, that includes:

* `packages/core` (TypeScript host + WASM boot + C# compilation/runtime bridge)
* `packages/core-bcl/System.Console` (the forked console layer)
* `packages/cli` (Node CLI that wires `msbuild-lite` + `nuget` + `core`)
* `packages/msbuild-lite` (bounded MSBuild-ish project evaluator)
* `packages/nuget` (bounded NuGet restore + allowlist + safety gate)
* `packages/refs-net10.0` (ref-pack fetch/extract tooling + manifest)
* `docs/` (I used it mainly to sanity-check intended behavior vs what’s implemented)

This report is opinionated and “debugger-grade”: it focuses on correctness, lifetime/cleanup, determinism, host interop failure modes, and security boundaries you appear to care about (even if the overall project is explicitly “not hardened against adversarial code”).

---

## Overall architecture review

At a high level, the system is cleanly layered:

* **`@carbide/core` (TS)** owns runtime boot (dotnet.js), host abstraction, and a friendly Session/Project API.
* **`Carbide.Core` (C#)** owns the Roslyn workspace/project model, compilation, diagnostics shaping, and (in T2/T3) terminal bridging.
* **`msbuild-lite`** provides a bounded evaluator for `.csproj` that produces a deterministic model (inputs, package refs, project refs).
* **`nuget`** resolves and fetches NuGet packages with an allowlist + safety checks, and emits a lock file with hashes.
* **CLI** composes the above and provides human/json output and diagnostics formatting.

This is a good decomposition. The internal boundaries are explicit, the “boundedness” is a consistent theme across packages, and the drift docs show you’re actively tracking intentional divergences.

---

## What’s especially strong

### 1) Clear “bounded MSBuild / bounded NuGet” story

* `msbuild-lite` refuses dangerous/complex MSBuild surface (tasks/targets/SDK resolution), but still supports enough to be useful for many repos.
* `nuget` has a *default* strict allowlist and a safety gate, and it persists content hashes in a lock file.

That’s the right direction for a tool that compiles arbitrary user projects without “running MSBuild”.

### 2) TS ↔ C# schema versioning is pragmatic

You’ve put schema version fields in the wire DTOs, accept older versions in C# (`ValidateSchemaVersion` allows a range), and TS accepts current/previous. That’s realistic for publishing NPM + NuGet-ish pieces that can drift.

### 3) The interactive terminal layering is well thought through

* JS bridge installs `globalThis.Carbide.Terminal.*`.
* C# uses `JSImport` to call that bridge.
* The System.Console fork provides a reasonable “ANSI terminal” behavior for color/cursor APIs without trying to emulate OS console handles.

It’s the right shape for WASM, and your comments are unusually good (which matters a lot here).

### 4) Tests exist where they matter

The `packages/core/test` node and browser tests hit determinism, compilation, runtime boot, and several integration edges. That’s a nontrivial amount of coverage for a cross-language WASM toolchain.

---

## Priority findings and recommendations

I’m grouping these by impact. The first group contains things I’d treat as “fix soon” because they’re correctness or security-footgun level.

---

## Critical issues

### C1) `AssemblyResolve` handler can leak if `Assembly.Load` fails

**Where**

* `packages/core/src/Services/ProjectCompiler.cs`

  * `RunAsync(...)`
  * `RunInteractiveAsync(...)`

**What happens**
Both run paths do:

1. `AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;`
2. `var assembly = LoadAssembly(peBytes);`  *(or similar)*
3. Only later enter a `try/finally` that removes the handler.

If `LoadAssembly(...)` throws (bad PE bytes, OOM, loader edge-case), you never reach the later `finally`, so the resolve handler remains attached for the remainder of the runtime.

**Why this matters**
Even if rare, leaked `AssemblyResolve` handlers are brutal to debug: they change subsequent resolution behavior and keep captured state alive. In a long-lived browser session, this can turn into “everything is weird after one failed run”.

**Recommendation**
Wrap the entire region after subscription (including `LoadAssembly` and EntryPoint reflection) in a `try/finally` that always unsubscribes.

Sketch (pattern only):

```csharp
AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;
try
{
    var assembly = LoadAssembly(peBytes);     // if this throws, handler is still removed
    var entry = assembly.EntryPoint ?? throw ...;

    // existing invoke/capture logic...
}
finally
{
    AppDomain.CurrentDomain.AssemblyResolve -= resolveHandler;
}
```

Then remove the inner “unsubscribe” from the later `finally` (or keep it redundantly; removing twice is harmless but noisy).

---

### C2) JS line editor flushes the wrong buffer when leaving key mode

**Where**

* `packages/core/src/ts/terminal/line-editor.ts`, `setKeyMode(enabled: boolean)`

**What I saw**
When leaving key mode (`enabled === false`), it does:

```ts
if (buffer.length > 0) {
  deliverStdIn(projectId, true, buffer);
  buffer = "";
}
```

But `buffer` is the **line-mode** buffer (the partially typed line). Key-mode input is already passed through raw immediately and is tracked on the **C# side** via `BrowserTerminalReader._partialKeyModeBuffer`.

**Why this matters**
This is behaviorally wrong in at least one common scenario:

* user types `"hel"` in line mode (buffer = `"hel"`)
* program switches to key mode (ReadKeyAsync)
* program switches back to line mode
* `setKeyMode(false)` fires and pushes `"hel"` as raw key-mode bytes to C#
* the partially typed line is lost, and those bytes can be prepended to the next line unexpectedly

That’s the kind of bug that shows up as “my input randomly duplicates or disappears”.

**Recommendation**
Delete this flush. The C# side already has the correct “leftover key-mode bytes” mechanism, and the JS line buffer should remain intact across mode toggles unless you explicitly want to discard it.

If you *do* want special behavior, keep two buffers: one for line mode and one for key-mode remainder. Right now you have only one, and it represents line mode.

---

### C3) `TerminalSession.dispose()` is documented as “safe mid-run”, but currently isn’t

**Where**

* `packages/core/src/ts/types.ts` docs for `TerminalSession.dispose()`
* `packages/core/src/ts/terminal/session.ts` (`teardown()` → `uninstallBridge()`)
* `packages/core/src/CompilationInterop.cs` (`DisposeTerminal` is currently a no-op)
* `packages/core/src/Terminal/StreamingStdOutWriter.cs` / `StderrSink` call into JSImport with no guard

**What’s happening**
The TS teardown removes `globalThis.Carbide.Terminal`. If the user program continues to write output after that, your JSImport calls can start throwing (depending on JSImport behavior when the target function is missing), which can crash the run.

The doc comment implies that the C# side “observes teardown signal on next flush attempt and unwinds cleanly”. There is no such signal today: `DisposeTerminal` is a stub.

**Why this matters**
Even if users don’t call `dispose` often, the mismatch between contract and reality will bite you later, and it’s exactly the kind of issue that makes consumers distrust the API.

**Recommendations (pick one consistent contract)**

1. **Make disposal truly safe (recommended)**

   * Keep `globalThis.Carbide.Terminal` installed but switch it to a no-op sink when disposed.
   * Or wrap `CarbideTerminalInterop.Write*` calls in try/catch and silently drop output if the bridge is missing.
   * Optional: implement `DisposeTerminal(projectId)` in C# to flip a per-run flag so writers stop calling into JS.

2. **Change the contract**

   * If mid-run dispose is not supported, say so explicitly and only uninstall the bridge after `exitPromise` resolves.

Given the current shape, option (1) is the easiest and matches your documentation intent.

---

### C4) Asset server path guard is vulnerable to “prefix” path traversal

**Where**

* `packages/core/src/ts/host/node/asset-server.ts`

**What I saw**
The guard is:

```ts
if (!abs.startsWith(rootAbs)) return null;
```

This is a known pitfall: `/foo/barbaz/file` starts with `/foo/bar`, but is not inside `/foo/bar`. On Windows, case and separator issues make this worse.

**Why this matters here**
Even though the server binds to `127.0.0.1`, *your own user code* running under Carbide can use HttpClient/fetch to request URLs from that server. If the guard can be bypassed, arbitrary local files may become readable to user code. This is exactly the “untrusted code can now read my disk” failure mode people fear, even if you’ve told them the sandbox isn’t hardened.

**Recommendation**
Replace the check with a `path.relative` based containment check, and also ensure separator-boundary correctness:

```ts
const rel = path.relative(rootAbs, abs);
if (rel.startsWith("..") || path.isAbsolute(rel)) return null;
return abs;
```

This is the standard safe pattern across platforms.

---

## Major issues

### M1) NuGet lock replay bypasses allowlist and safety checks

**Where**

* `packages/nuget/src/resolver.ts`, `resolve(...)`:

```ts
if (opts.lock) {
  return replayLock(opts.lock, ...);
}
applyAllowList(...)
checkSafety(...)
```

**What this means**
If a lock file is used, **you skip allowlist and safety** entirely.

**Why it matters**

* If a user ever generated a lock under “advisory” (or “off”), then switches to “strict”, the strict mode won’t be enforced.
* If an attacker can swap the lock file (even locally), they can bypass allowlist entirely (hashes still protect against content changes, but they don’t protect against *choosing a disallowed package* with its own correct hash).

**Recommendations**

* Always enforce allowlist rules on lock replay unless allowListMode is explicitly `off`.
* Consider recording the allowListMode and safety policy in the lock itself and refusing to replay under stricter settings unless the user opts in (or forces regenerate).

At minimum, if you keep bypass semantics, surface an explicit warning in replay mode when allowListMode is strict (so users aren’t getting a silent downgrade).

---

### M2) NuGet “same depth” tie-break uses lexicographic compare, not semantic version compare

**Where**

* `packages/nuget/src/resolver.ts`, in `mergeGraph(...)`:

```ts
const isNewer = version.raw.localeCompare(existing.package.version) > 0;
```

**Why it’s wrong**
String compare is not semantic versioning:

* `"10.0.0"` compares *less than* `"2.0.0"` lexicographically.

You already have `compareVersion(...)` in `version-range.ts` and import it. This looks like an accidental regression.

**Recommendation**
Parse both versions once and use semantic comparison:

```ts
const a = parseVersion(version.raw);
const b = parseVersion(existing.package.version);
const isNewer = compareVersion(a, b) > 0;
```

If parsing fails, *then* fall back to localeCompare (as a last-resort).

---

### M3) Safety warning codes include “generators” but you don’t implement generator detection

**Where**

* `packages/nuget/src/warnings.ts` contains `MSNUGET018` (“source generator”)
* `packages/nuget/src/safety.ts` doesn’t emit it

**Recommendation**
Either:

* implement detection (usually: reject analyzer assemblies under `analyzers/dotnet/cs/` that reference `Microsoft.CodeAnalysis` generator APIs, or more conservatively reject *all* analyzers), **or**
* remove the warning code and doc references until you implement it.

Right now it reads like a promised safety property that doesn’t exist.

---

### M4) CLI `tree` command advertises JSON mode but doesn’t implement it

**Where**

* `packages/cli/src/commands/tree.ts`

The help text says:

* `--format json` emits the same structure as audit.
* default “human”

But implementation always prints the human tree and ignores `format` except for error handling.

**Recommendation**
Implement JSON output for `tree` by reusing the audit payload (or explicitly remove `--format` from tree until you do).

Also, note that your global `parseFormat` default is `"json"`, but tree claims human default; currently you get human because you ignore it — but that’s an accident, not a contract.

---

### M5) `carbide build --project` writes `durationMs` as `undefined` (bug)

**Where**

* `packages/cli/src/commands/build.ts`, in the failure JSON payload:

```ts
durationMs: root.model ? undefined : undefined
```

That expression is always `undefined`.

**Recommendation**
Either:

* compute duration like you do elsewhere (wall time for full graph), or
* remove the field from this particular error output.

But don’t emit a field that’s always undefined—it suggests a partially implemented schema.

---

### M6) Long-lived resource cleanup: `AdhocWorkspace` is never disposed

**Where**

* `packages/core/src/Services/ProjectCompiler.cs` constructs an `AdhocWorkspace` but `ProjectCompiler` is not `IDisposable`, and session/project disposal doesn’t free workspace resources.

**Why it matters**
In long-lived in-browser sessions, users can create and dispose many projects. Not disposing workspaces can lead to steady memory growth (Roslyn caches, services, etc).

**Recommendation**

* Make `ProjectCompiler` implement `IDisposable` and call `_workspace.Dispose()`.
* In `SessionSolutions.DisposeSession(...)` and a future `DisposeProject(...)`, dispose the compiler instances.

Even if Roslyn doesn’t leak badly here, explicit disposal is the safe pattern.

---

## Medium and minor issues

### m1) Out-of-date / conflicting comments and docs

A few cases where code and docs diverged:

* `packages/cli/src/commands/run.ts` has a comment claiming args after `--` aren’t forwarded; but the code **does** forward `programArgs` and `stdin` into `project.run(...)`.
* `docs/Carbide-Current-State-Guide.md` says argv/stdin aren’t wired; in this snapshot, they are (at least for non-interactive and interactive via `RunOptionsDto`/`RunInteractiveOptionsDto`).

Recommendation: treat docs as part of your API. If behavior changed, update the guide, otherwise consumers will cargo-cult workarounds you don’t want.

---

### m2) `TerminalInputState` has dead “cached constructor probe” fields

**Where**

* `packages/core/src/Terminal/TerminalInputState.cs`

These exist:

```csharp
private static object? s_cachedArgsFactoryProbe;
private static bool s_argsFactoryProbed;
```

…but nothing uses them meaningfully; and `CreateCancelEventArgs()` still reflects every time.

Recommendation:

* Either implement the caching you describe in the comment (cache `ConstructorInfo` and/or a compiled delegate),
* or delete the unused fields and adjust the comment.

---

### m3) BrowserTerminalReader TCS continuations could run inline (reentrancy)

**Where**

* `packages/core/src/Terminal/BrowserTerminalReader.cs`

You use `new TaskCompletionSource<string>()` without `RunContinuationsAsynchronously`.

In a single-threaded WASM runtime, inline continuations can cause deeply nested reentrancy (especially if JS callbacks enqueue data while C# awaits and resumes inline).

Recommendation:

* consider `TaskCreationOptions.RunContinuationsAsynchronously` unless you have a specific reason not to. It tends to make these producer/consumer readers more robust.

---

### m4) Performance: a couple of easy wins

None of these are “broken”, but they’ll show up in bigger repos / larger pastes:

* `tree.ts` uses `multi.subprojects.indexOf(sub)` inside a walk → O(n²).
  Fix: build a `Map<key, index>` once.
* `msbuild-lite` glob resolution walks the tree per pattern.
  If you ever hit big repos, consider caching the directory listing once per baseDir.
* `line-editor.ts` echoes per character; for large paste operations, batching contiguous printable text into fewer `terminal.write(...)` calls can drastically reduce overhead.

---

### m5) Determinism and output-kind behavior: comment drift

**Where**

* `ProjectCompiler.BuildAsync(...)` summary mentions “or a suitable Main”, but the current behavior is “ConsoleApplication only when top-level statements exist, else DLL” (which matches your drift note elsewhere).

Recommendation: update the comment so future readers don’t assume “Main detection” exists.

---

## Package-by-package notes

### `@carbide/core` TS API

* The `Session` and `Project` API shape is nice: it’s minimal but covers the important knobs (language version, references, args/stdin, interactive).
* The handle-based reference API is sensible for crossing the interop boundary.

One API hardening suggestion (optional): if you want to prevent consumers from forging handles, use a class with private fields (or a `Symbol` brand) rather than a plain object. Right now the runtime check (`id` + `sessionId` membership) mostly protects you anyway; this is more about UX and debugging.

### `Carbide.Core` C#

* The Roslyn workspace usage is straightforward and readable.
* Diagnostics shaping is good; you preserve location spans and IDs without being overly chatty.
* Your reflection hooks into the forked System.Console are well-contained (and guarded with fallbacks).

Main correctness fixes are the `AssemblyResolve` cleanup and interactive teardown behavior discussed above.

### `core-bcl/System.Console`

This is unusually well documented for a fork. The approach of emitting ANSI sequences for cursor/цвет is appropriate for xterm. A real limitation (not necessarily fixable) is that `OpenStandardOutput()` uses a naïve UTF8 decode per write; that can split multibyte sequences if somebody writes raw bytes. If you care, the fix is to keep a `Decoder` instance with state across writes (incremental decode). If you don’t care, a short doc note would help.

### `@carbide/msbuild-lite`

Given the bounded goals, the implementation is clean:

* XML parser is intentionally minimal and explicit.
* Condition evaluator is constrained.
* Refusal paths produce warnings (good) rather than silently accepting unsupported features.

If this grows, the biggest future risk is “users assume it’s MSBuild” and then get subtle mismatches. The current docs do a good job setting expectations; keep that discipline.

### `@carbide/nuget`

The allowlist + safety gate is a big positive. The key issues are:

* lock replay bypasses the gate,
* same-depth tie-break is wrong (lexicographic),
* generator safety code exists but isn’t implemented.

Fixing those will make this package feel a lot more trustworthy.

### `@carbide/cli`

The graph pipeline is surprisingly clear, especially how you attach producer outputs as metadata references downstream. The main CLI issues are the `tree --format json` mismatch and the `durationMs` buglet.

---

## Suggested “fix order” roadmap

If I were landing fixes with minimal churn:

### Day-1 fixes (high value, low risk)

1. Fix `AssemblyResolve` handler cleanup in both run paths.
2. Remove the incorrect buffer flush in `line-editor.ts setKeyMode(false)`.
3. Make interactive teardown safe:

   * easiest: don’t delete `globalThis.Carbide.Terminal`; replace with no-op functions, or catch JS interop exceptions in writers.
4. Fix asset server containment check using `path.relative`.

### Next wave

5. Enforce allowlist/safety on lock replay (or warn loudly and document as “trusted lock only”).
6. Replace lexicographic version compare with semantic compare.
7. Implement / remove generator safety warning.
8. Fix CLI `tree --format json` (either implement JSON or remove flag).
9. Fix CLI `durationMs` undefined emission.

### Longer-term improvements

10. Dispose `AdhocWorkspace` and any other long-lived resources on session/project disposal.
11. Consider a more realistic `SynchronizationContext` that *actually* yields to the JS loop (so `Task.Yield` and some async patterns behave closer to expectations), if you want broader compatibility.

---

## Closing thought

This codebase has a rare combination of: (1) a very clear “bounded scope” philosophy, (2) strong documentation, and (3) real tests around the dangerous edges (WASM boot, determinism). That puts you in a good position to tighten a few correctness/security footguns without having to rethink the architecture.