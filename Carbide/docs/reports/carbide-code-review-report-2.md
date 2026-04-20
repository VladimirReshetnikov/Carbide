# Carbide Code Review Report

Date: 2026-04-20

## Scope and method

I reviewed the uploaded project as a standalone slice of a larger monorepo. The review focused on correctness, runtime lifecycle behavior, API consistency, CLI behavior, MSBuild-lite project evaluation, NuGet resolution, security hardening, documentation drift, and maintainability.

I did not modify the project. I also could not run the full build/test suite in this container because the .NET SDK is not installed and the Node dependencies are not restored. A quick TypeScript compile attempt with the globally installed `tsc` reached dependency setup first: `TS2688: Cannot find type definition file for 'node'`. The findings below are therefore based on static analysis of the source and bundled tests/docs, not on an executed test run.

## Executive summary

Carbide has a strong architecture for an ambitious browser/Node-hosted C# compiler/runtime slice: the layering is clean, the public TypeScript API is compact, the JSON interop boundary is explicit, and the MSBuild/NuGet implementations are intentionally bounded rather than pretending to be full clones. That boundedness is healthy.

The main risks are in three places:

1. Runtime execution mutates global process state (`Console`, `SynchronizationContext`, `AssemblyResolve`, `AppContext`) without a single top-level cleanup guard. Several exceptional and early-return paths can leak state into the next run.
2. The interactive terminal lifecycle promises stronger semantics than the implementation provides. `dispose()` detaches the JS side but currently does not cancel/unblock the C# side, so stuck reads can leave `exitPromise` unresolved.
3. The bounded MSBuild and NuGet layers are close, but have several policy bugs: multi-TFM selection is sorted despite claiming “first listed”; NuGet same-depth conflict resolution uses string comparison for versions; lock replay bypasses allow-list/safety checks.

There is also a real localhost security bug in the Node asset server’s path traversal check: it uses `startsWith(rootAbs)` instead of a path-relative containment check.

## Top recommended fix order

1. Put all runtime global-state mutations under robust `try/finally` cleanup and serialize run/interactive execution.
2. Implement real interactive-session cancellation/disposal on the C# side and align the TypeScript contract with behavior.
3. Fix the Node asset-server path containment check and add a prefix-sibling traversal regression test.
4. Fix MSBuild-lite multi-TFM ordering and property substitution in item attributes.
5. Fix NuGet version conflict resolution and apply safety/allow-list checks during lock replay.
6. Bring the root README and current-state guide back in sync with the actual implemented milestone surface.

---

# Detailed findings

## 1. Critical/High: runtime global state can leak after failures

### Evidence

`packages/core/src/Services/ProjectCompiler.cs`

In `RunAsync`:

- `AssemblyResolve` is subscribed before the main invocation `try/finally`: around line 406.
- `LoadAssembly(peBytes)` and entry-point lookup happen before the cleanup `finally`: around lines 408-421.
- The cleanup that restores `Console` and unsubscribes `AssemblyResolve` is only in the later `finally`: around lines 490-501.

If assembly loading or entry-point reflection throws, the resolve handler is left installed. Console redirection happens later, so that part is not leaked on this exact path, but the resolver is.

In `RunInteractiveAsync`:

- A `TerminalInputState` is created and a sync context is installed before compile/emit early returns: around lines 572-587.
- Compile/emit failures can return around lines 591-600, before sync-context restoration.
- `AssemblyResolve` is subscribed before `LoadAssembly(peBytes)` and entry-point lookup: around lines 640-645.
- Full cleanup happens only in a later `finally`: around lines 744-762.

The result is that compile failure can leak the `SynchronizationContext`, and load/reflection failure can leak both the `AssemblyResolve` handler and the sync context.

### Impact

This is especially risky in a long-lived browser/.NET WASM session. A failed run can corrupt subsequent runs in ways that are hard to diagnose:

- assembly binding can resolve against references from the wrong project/run;
- `SynchronizationContext.Current` may remain Carbide’s context outside the intended session;
- interactive input state and AppContext bridge state may outlive their run.

### Recommendation

Use one top-level state-restoration guard per run, immediately after any global state is mutated. Avoid returning from inside the guarded region; store the result and return after cleanup.

A robust shape would be:

```csharp
var oldOut = Console.Out;
var oldErr = Console.Error;
var oldSyncContext = SynchronizationContext.Current;
bool resolveSubscribed = false;
TerminalInputState? inputState = null;

try
{
    // mutate globals only inside this block
    SynchronizationContext.SetSynchronizationContext(CarbideSyncContext.Instance);
    AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;
    resolveSubscribed = true;

    // compile, emit, load, invoke, drain
}
finally
{
    if (resolveSubscribed)
        AppDomain.CurrentDomain.AssemblyResolve -= resolveHandler;

    Console.SetOut(oldOut);
    Console.SetError(oldErr);
    RestoreConsoleIn(oldIn);
    SynchronizationContext.SetSynchronizationContext(oldSyncContext);
    AppContext.SetData("Carbide.InteractiveBridge", null);
    inputState?.Dispose();
}
```

Also consider extracting a small `RunStateScope`/`ExecutionScope` type so the cleanup discipline becomes mechanical rather than hand-written in two long methods.

---

## 2. High: `TerminalSession.dispose()` does not implement its public contract

### Evidence

`packages/core/src/ts/types.ts` documents:

- `exitPromise` resolves when a session exits or is disposed: around lines 147-152.
- `dispose()` is safe mid-run and awaits in-flight run/drain: around lines 157-162.

`packages/core/src/ts/terminal/session.ts` implements `dispose` as `teardown`: around lines 89-92. `teardown` calls `interop.DisposeTerminal(projectId)` and then detaches resize/editor/sink/bridge: around lines 63-75. It does not await `exitPromise`.

`packages/core/src/Services/SessionSolutions.cs` has `DisposeInteractive` as a no-op: around lines 169-179.

### Impact

A user program blocked in `Console.ReadLine()`/`ReadLineAsync()` may remain blocked after `dispose()`. The JS terminal objects are detached, so no further input can arrive, and `exitPromise` can hang indefinitely. This is exactly the kind of lifecycle bug that makes browser demos feel flaky.

### Recommendation

Implement `DisposeTerminal(projectId)` on the C# side so it can find the active `TerminalInputState` and complete/cancel it. The terminal reader should observe completion/cancellation and return EOF or throw an operation-cancelled signal that the run loop converts into a normal disposed result.

Then decide and document one of these exact contracts:

- `dispose(): Promise<void>` waits until C# has observed cancellation and the run has drained; or
- `dispose(): void` only detaches, while `exitPromise` remains the way to await completion.

The current TypeScript declaration promises the first shape, while the implementation behaves closer to the second, except that the C# run may never complete.

---

## 3. High: run paths are not concurrency-safe

### Evidence

`ProjectCompiler.RunAsync` and `RunInteractiveAsync` mutate global process state:

- `Console.Out` and `Console.Error`;
- internal `Console.In` through reflection;
- `AppDomain.CurrentDomain.AssemblyResolve`;
- `AppContext.SetData("Carbide.InteractiveBridge", ...)`;
- `SynchronizationContext.Current`.

The TypeScript `Project` object only prevents a second interactive run for the same project:

- `packages/core/src/ts/project.ts`, `_activeInteractive`: around lines 16-23;
- interactive guard: around lines 124-129.

It does not prevent two `project.run()` calls, two runs from different projects, or a non-interactive run overlapping an interactive run.

### Impact

Concurrent runs can cross-wire console streams, assembly binding, terminal bridges, and sync contexts. Since these are process-wide/global in the WASM runtime, this is a correctness issue, not just a race-performance issue.

### Recommendation

Introduce an execution semaphore around run/interactive invocation. If concurrent execution is intentionally unsupported, fail fast with a clear error. Builds can remain concurrent only if the underlying Roslyn workspace and reference caches are made safe for that usage.

---

## 4. High/Medium: `OutputKind` inference contradicts comments and likely emits non-executable builds

### Evidence

`packages/core/src/Services/ProjectCompiler.cs`

The comment near `CompileCoreAsync` says build mode should auto-select `ConsoleApplication` if top-level statements or a suitable `Main` exists: around lines 250-258.

The implementation only checks for `GlobalStatementSyntax`:

- `InferOutputKind`: around lines 282-296.

A project using conventional `static Main` but no top-level statements will be compiled as a DLL by `build()`. In contrast, `run()` forces `OutputKind.ConsoleApplication`, so `run()` and `build()` disagree.

### Impact

A normal C# console program with `static int Main(string[] args)` can run but build as a library with no executable entry point. This is surprising and contradicts the in-code documentation.

### Recommendation

Either:

- use Roslyn entry-point detection after trying a console compilation;
- implement real `Main` signature detection; or
- expose `ProjectOptions.outputKind` and make build behavior explicit.

A practical approach is to first create a console compilation and call `GetEntryPoint(cancellationToken)`. If an entry point exists, keep console output kind; otherwise compile as DLL. Avoid brittle syntax-only heuristics.

---

## 5. High: Node asset-server path traversal check is prefix-based

### Evidence

`packages/core/src/ts/host/node/asset-server.ts`

The server resolves a requested path and checks containment using string prefix logic:

- `rel`, `abs`, `rootAbs`: around lines 54-56;
- `if (!abs.startsWith(rootAbs))`: around lines 57-58.

If the root is `/tmp/root`, a sibling path like `/tmp/root2/file` also starts with `/tmp/root`. A request of the form `/../root2/file` can pass this check after path resolution.

The existing test covers a direct sibling basename and null-byte handling, but not a same-prefix sibling directory:

- `packages/core/test/node/asset-server.test.mjs`: around lines 26-47.

### Impact

The server is localhost-only, but it serves runtime/ref-pack DLLs and is unauthenticated local HTTP. A path traversal bug here can expose adjacent files under predictable local paths.

### Recommendation

Use `path.relative` for containment:

```ts
const rootAbs = path.resolve(root);
const abs = path.resolve(rootAbs, rel);
const relToRoot = path.relative(rootAbs, abs);

if (relToRoot.startsWith("..") || path.isAbsolute(relToRoot)) {
  res.writeHead(403);
  res.end();
  return;
}
```

Also restrict methods to `GET`/`HEAD`, and add a regression test with root `/tmp/.../root` and sibling `/tmp/.../root2/secret.txt`.

---

## 6. High/Medium: MSBuild-lite “first-listed TFM wins” policy is broken by sorting

### Evidence

`packages/msbuild-lite/src/index.ts`

- `const uniqueTfms = [...new Set(ctx.tfms)].sort();`: around line 147.
- The evaluation trace says `selectionPolicy: "first-listed"`: around lines 160-164.

The test encodes the current behavior while naming it the opposite:

- `packages/msbuild-lite/test/parse.test.mjs`, test “first-listed wins”: around lines 64-79.
- Source project lists `net8.0;net10.0`, but the assertion expects sorted `targetFrameworks` and selected `net10.0`.

### Impact

For a multi-targeted project, Carbide may restore/compile against a different TFM than the project author intended. This can change package asset selection, references, analyzers/build behavior, language defaults, and source inclusion conditions.

### Recommendation

Preserve first occurrence order:

```ts
const uniqueTfms = [...new Set(ctx.tfms)];
const targetFramework = uniqueTfms[0] ?? DEFAULT_TFM;
```

If a sorted display list is useful, store it separately. Fix the test so it actually proves first-listed behavior.

---

## 7. High/Medium: NuGet same-depth conflict resolution uses string comparison for versions

### Evidence

`packages/nuget/src/resolver.ts`

- The same-depth conflict logic chooses `version.raw.localeCompare(existing.package.version) > 0`: around lines 91-94.
- `compareVersion`/`parseVersion` are already imported near the top but not used here.

### Impact

NuGet version semantics are not lexicographic string semantics. For example, `2.10.0` and `2.9.0` can be ordered incorrectly by string comparison. Pre-release versions are even more sensitive.

### Recommendation

Use the existing semantic version comparison helpers:

```ts
const existingVersion = parseVersion(existing.package.version);
if (compareVersion(version, existingVersion) > 0) {
    // choose newer semantic version
}
```

Also update the warning text to say “semantic higher version” rather than “lexicographically later version”.

---

## 8. High/Medium: NuGet lock replay bypasses allow-list and safety checks

### Evidence

`packages/nuget/src/resolver.ts`

- Lock replay short-circuits fresh resolution: around lines 58-60.
- `replayLock` downloads packages, checks SHA-256, reads nuspec/entries, and picks lib assets: around lines 322-328 and following.
- Fresh resolution applies allow-list and safety checks: around lines 71-76 and 117-121.
- `replayLock` does not call the equivalent policy checks.

### Impact

A stale or malicious lock file can replay packages that current policy would reject: disallowed top-level packages, native assets, analyzers, build targets, or other unsafe package shapes. The SHA check proves identity relative to the lock; it does not prove the package is acceptable under current policy.

### Recommendation

Apply allow-list and safety policy during lock replay by default. If there is a need for a fully trusted lock mode, make it explicit and noisy. Integrity verification is still valuable, but it should be an additional check, not a substitute for policy enforcement.

---

## 9. Medium: item attributes are not property-substituted in MSBuild-lite

### Evidence

`packages/msbuild-lite/src/evaluator.ts`

Item attribute values are stored raw:

- `PackageReference Include` and `Version`: around lines 303-307;
- `ProjectReference Include`: around lines 315-319;
- `Compile Include`/`Remove`: around lines 330-342.

The evaluator does perform property substitution in property values, conditions, and import paths, so this omission is inconsistent.

### Impact

Common project patterns will not evaluate correctly:

```xml
<PropertyGroup>
  <NewtonsoftVersion>13.0.3</NewtonsoftVersion>
  <GeneratedDir>obj\Generated</GeneratedDir>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" Version="$(NewtonsoftVersion)" />
  <Compile Include="$(GeneratedDir)\*.cs" />
</ItemGroup>
```

### Recommendation

Run the same `substituteVars` machinery over relevant item attributes before storing them. This should include at least `Include`, `Update`, `Remove`, `Version`, `Exclude`, and `PrivateAssets`/`IncludeAssets`/`ExcludeAssets` if those are added later.

---

## 10. Medium: missing `ProjectReference` diagnostics lose referrer context

### Evidence

`packages/cli/src/project-file.ts`

The project graph work queue only stores `absPath` and `isRoot`: around lines 111-116. If `parseCsproj` fails for a referenced project, the catch block throws `ProjectReferenceNotFoundError(absPath, absPath, absPath)`: around lines 119-128.

A later guard can include referrer/include context, but missing files fail during parse before that later context is used: around lines 170-180.

### Impact

A user gets a worse error message for one of the most common project-file failures. Instead of “project A references missing project B through include C”, the diagnostic can degenerate into “B references B through B”.

### Recommendation

Carry parent/referrer and original include string in the work item:

```ts
type WorkItem = {
  absPath: string;
  isRoot: boolean;
  referrerAbsPath?: string;
  includePath?: string;
};
```

Then use those fields when producing `MSPROJ004`.

---

## 11. Medium: invalid CLI flag values are classified as internal errors

### Evidence

`packages/cli/src/format.ts`

- `parseFormat` throws generic `Error`: around lines 9-13.

`packages/cli/src/nuget-options.ts`

- invalid `--allow-list-mode` throws generic `Error`: around lines 27-33.

`packages/cli/src/errors.ts`

- only known argument/log/conflict errors are classified as flag errors: around lines 152-164;
- generic errors become internal errors: around lines 165-174.

### Impact

A user typo such as `--format xml` or `--allow-list-mode maybe` can be reported as an internal error rather than a flag/usage error.

### Recommendation

Throw `ArgParseError` for invalid flag values, or add dedicated error classes and classify them as `flag-error` with exit code 3.

---

## 12. Medium: reference and metadata caches are not thread-safe or idempotent

### Evidence

`packages/core/src/Services/MetadataReferenceCache.cs`

- `_metadataReferences` is a mutable `List<MetadataReference>`: around line 14.
- `AddReference` appends without lock or de-duplication: around lines 18-20.

`packages/core/src/Services/SessionSolutions.cs`

- `_referencesLoaded` is a plain bool: around lines 18-24 and line 52.

### Impact

Concurrent bootstrapping can duplicate references or observe partially initialized reference state. Later initialization with a different asset list is ignored once `_referencesLoaded` is true, which can surprise embedders that expect per-session/per-project reference selection.

### Recommendation

Make reference initialization explicitly singleton and thread-safe with `SemaphoreSlim` or a lock. De-duplicate references by path/identity/hash. If different target frameworks or reference packs will be supported, key the cache by asset set rather than using a single global boolean.

---

## 13. Medium: HTTP metadata reference loading silently skips failed downloads

### Evidence

`packages/core/src/Services/WasmMetadataReferenceResolver.cs`

`ResolveWithBytesAsync` intentionally avoids `EnsureSuccessStatusCode`; it reads whatever body comes back and returns `(bytes, null)` when metadata validation fails: around lines 30-42.

### Impact

An HTTP 404/500 response can become “skipped invalid metadata” during initialization, followed later by confusing compiler errors because core references are missing.

### Recommendation

For `http`/`https`, require success status codes and include the URL/status in the error. The `file://` workaround for browser-hosted asset fetching can remain special-cased if needed, but actual HTTP failures should be hard failures.

---

## 14. Medium: packages with no compatible lib folder succeed silently

### Evidence

`packages/nuget/src/resolver.ts`

- `pickBestLibFolder` may return `null`: around lines 123-128.
- The package is still added to the graph, but with no references.

### Impact

This is fine for metapackages. It is confusing for a direct package reference that has `lib/` assets but none compatible with the target TFM. The user will later see missing namespace/type errors rather than a restore-time explanation.

### Recommendation

Warn for top-level packages with no compatible lib asset. Consider failing when the package has lib assets but none match the target and the package is a direct dependency.

---

## 15. Medium/Security hardening: package IDs and versions are used as cache path/URL segments without validation

### Evidence

`packages/nuget/src/cache.ts`

- `packageDir(id, version)` path-joins raw lowercased id/version: around lines 107-109;
- `nupkgFileName` uses raw id/version: around lines 110-112.

`packages/nuget/src/flat-container.ts`

- URLs are constructed directly from lowercased `id` and `version`: around lines 50 and 70-71.

### Impact

NuGet.org IDs and normalized versions are constrained, but custom sources, hand-authored lock files, or offline-cache modes can feed unexpected strings. Slashes or `..` segments could produce path traversal or malformed URLs.

### Recommendation

Validate package IDs and normalized versions before using them in path or URL construction. Also enforce cache-root containment using `path.relative` before reading/writing.

---

## 16. Medium/Low: terminal cancellation registrations are not disposed on normal completion

### Evidence

- `packages/core/src/Terminal/BrowserTerminalReader.cs`, `WaitForBytesAsync`: around lines 99-110.
- `BrowserTerminalReader.ReadLineAsync`: around lines 172-183.
- `packages/core/src/Terminal/CarbideConsole.cs`, `WaitForResizeAsync`: around lines 370-378.
- `CarbideConsole.DelayAsync`: around lines 334-344 has a race where cancellation before timer completion can retain registration until token disposal.

### Impact

This is not catastrophic for short runs, but in long interactive sessions or repeated waits, cancellation registrations can accumulate unnecessarily.

### Recommendation

Dispose registrations when the task completes, regardless of whether completion came from data, resize, timeout, or cancellation. A helper around `TaskCompletionSource` plus `CancellationTokenRegistration` would reduce copy/paste.

---

## 17. Medium/Low: zip readers have weak bounds/integrity checks

### Evidence

`packages/nuget/src/zip.ts`

- `listEntries` reads central directory structures directly without prechecking every offset: around lines 51-71.
- `readEntry` does not validate `dataStart + compressedSize`, inflated size, or CRC: around lines 77-95.

`packages/refs-net10.0/scripts/build.mjs` uses a similar minimal zip parser: around lines 100-163.

### Impact

Malformed packages/ref packs can produce raw `RangeError`s, truncated data, or corrupted extracted files. In practice, downloads are usually trusted NuGet packages, but this code is part of the package trust boundary.

### Recommendation

Add explicit bounds checks and throw domain-specific `ZipParseError`s. Verify inflated byte length and, preferably, CRC32 for package contents.

---

## 18. Low/Medium: XML entity decoder omits numeric entities

### Evidence

`packages/msbuild-lite/src/xml.ts`

`decodeEntities` handles only `&quot;`, `&apos;`, `&lt;`, `&gt;`, and `&amp;`: around lines 257-267.

### Impact

Numeric character references such as `&#x5C;` or `&#92;` remain encoded. This can affect paths, property values, and conditions. It is not common, but it is valid XML.

### Recommendation

Support numeric XML character references, or use a small battle-tested XML tokenizer while keeping the evaluator bounded.

---

## 19. Low/Medium: compile glob expansion repeatedly walks the filesystem

### Evidence

`packages/msbuild-lite/src/compile-items.ts`

- `discoverCsFiles` walks once for defaults: around lines 13-18.
- `expandGlob` calls `collectAllFiles` every time: around lines 50-56.
- `resolveCompileItems` calls `expandGlob` for each item op: around line 167.

### Impact

Large repos with many include/remove operations pay `O(itemOperations × files)` filesystem traversal cost.

### Recommendation

Collect candidate files once, then match all glob operations against the cached list.

---

## 20. Low: `TerminalInputState.FireCancelKeyPress` swallows handler exceptions

### Evidence

`packages/core/src/Terminal/TerminalInputState.cs`

- instance handler exceptions are caught and ignored: around lines 137-146;
- reflected forked-console handler invocation also swallows exceptions: around lines 188-198.

### Impact

This hides bugs in user code and differs from the usual expectation that event handler failures are observable unless explicitly isolated.

### Recommendation

If swallowing is intentional for terminal robustness, document it and add tests. Otherwise, route exceptions to stderr or surface them through the run result.

---

## 21. Low/Security hardening: terminal title writes raw OSC content

### Evidence

`packages/core/src/Terminal/CarbideConsole.cs`

- `Title` writes `\x1b]0;{value}\x07`: around lines 190-197.

### Impact

If the title value contains BEL, ESC, or other C0 controls, it can inject additional terminal controls.

### Recommendation

Strip or encode BEL, ESC, and control characters from titles before emitting OSC. If raw OSC emission is useful for advanced scenarios, expose it through an explicit lower-level API rather than `Console.Title`.

---

## 22. API consistency: `ProjectOptions.targetFramework` is accepted but ignored by core compilation

### Evidence

- TypeScript sends `targetFramework` in `packages/core/src/ts/session.ts`: around lines 52-63.
- The interop DTO includes `TargetFramework` in `CompilationInterop.cs`: around lines 302-311.
- `BuildDocumentOptions` ignores `dto.TargetFramework`: around lines 266-298.
- `DocumentOptions.cs` has no target-framework property.

### Impact

Direct users of `@carbide/core` may believe `targetFramework: "net8.0"` changes references or compilation behavior, but the core compiler path ignores it. The CLI does use TFM for NuGet/project-file resolution, so behavior depends on the entry point.

### Recommendation

Either remove/make-internal the option for direct core API users, or make it select a ref pack/runtime asset set and affect compilation deterministically. At minimum, document the current behavior precisely.

---

## 23. Build/install reproducibility: `refs-net10.0` postinstall downloads when generated files are absent

### Evidence

`packages/refs-net10.0/package.json`

- `postinstall` runs `node scripts/build.mjs`.

`packages/refs-net10.0/scripts/build.mjs`

- the ref-pack URL is hard-coded: around line 20;
- the expected hash is intentionally unpinned: around lines 21-23;
- if `ref-manifest.json` is absent, the script downloads/extracts assets: around lines 165-189.

In the uploaded tree, the generated manifest and DLLs are not present.

### Impact

For source development this may be convenient. For a published package, install-time network fetches are offline-hostile and less reproducible. Without a pinned hash, they are also weaker from a supply-chain perspective.

### Recommendation

Ensure the publish pipeline runs the script before packing and includes generated ref DLLs/manifest. Prefer making `postinstall` a verification/no-op path for published packages. Pin the expected NuGet package SHA-256 in the script.

---

## 24. Documentation drift is significant

### Evidence

`README.md`

- says the current tree is working through M6: around line 5;
- says ProjectReference orchestration is pending: around line 15;
- says ProjectReference is captured-only in CLI docs: around line 133.

`docs/Carbide-Current-State-Guide.md`

- says M9/M11/U1-U3/T1-T3 are implemented, including ProjectReference orchestration and interactive argv/stdin: around lines 25-28;
- later says `Directory.Build.props`, explicit `<Import>`, live output, stdin, and argv are not implemented: around lines 43-49 and 336-340.

`packages/cli/README.md`

- appears closer to the actual CLI state, describing U2 and M9 behavior around lines 50 and 91.

### Impact

The project is evolving quickly, and the docs have become a liability. New contributors will not know which surface is authoritative, and users may avoid implemented features or depend on stale limitations.

### Recommendation

Make one current-state document authoritative and update root README to reference it. I would specifically revise:

- root README milestone summary;
- ProjectReference status;
- Directory.Build.props / Import status;
- interactive stdin/argv/live-output status;
- implemented vs intentionally-bounded MSBuild/NuGet limitations.

A small docs consistency check could catch phrases like “pending”, “not yet implemented”, and old milestone labels after implementation PRs.

---

# Positive observations

The project has several strong qualities worth preserving:

- The TypeScript public API is compact and pleasant: session → project → build/run/interactive is easy to reason about.
- The C# interop DTOs are explicit and mostly stable-looking.
- The CLI has a structured JSON sentinel path, which is excellent for CI/editor integration.
- The MSBuild-lite design is appropriately scoped. It does not pretend to be full MSBuild, which is the right tradeoff for this kind of project.
- The NuGet resolver already has concepts for allow-lists, strict package safety, lock files, source abstraction, and cache boundaries. The model is solid; the policy holes above are fixable.
- The terminal stack has a thoughtful shape: bridge, writer, reader, input state, raw mode, resize, and Ctrl+C handling are separated rather than tangled together.

# Suggested regression tests

1. `RunAsync` cleanup test: force `LoadAssembly` or entry-point lookup failure and assert `AssemblyResolve` is not left subscribed and console streams are restored.
2. `RunInteractiveAsync` compile-failure cleanup test: bad source should not leave `SynchronizationContext.Current` changed.
3. Interactive disposal test: program blocks on `Console.ReadLineAsync`; `dispose()` causes `exitPromise` to resolve.
4. Concurrent-run test: either verify serialization or verify a clear “run already active” error.
5. Asset-server traversal test: root directory named `root`, sibling `root2`, request `/../root2/secret.txt` must fail.
6. Multi-TFM test: `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` selects `net8.0`.
7. MSBuild property substitution tests for `PackageReference Version`, `ProjectReference Include`, and `Compile Include/Remove`.
8. NuGet conflict test: same-depth dependencies `2.9.0` vs `2.10.0` choose `2.10.0`.
9. Lock replay policy test: a locked package with unsafe native/build/analyzer assets is rejected under strict policy.
10. CLI invalid flag tests: invalid `--format` and `--allow-list-mode` report `flag-error`, not `internal`.
11. Package path validation test: package IDs/versions containing slash or `..` are rejected before cache path construction.
12. HTTP reference loader test: 404/500 surfaces a clear initialization error.

# Final assessment

Carbide is in a good architectural place, but I would treat the runtime lifecycle issues as release blockers. They are subtle because the happy path works, but once a browser/WASM process gets into a bad global state, later failures will look random. The next-most-important work is to make the bounded project/restore semantics deterministic and honest: first-listed TFM should mean first-listed, version comparison should be semantic, lock replay should still honor safety policy, and docs should match the product.

None of the problems require a redesign. They are mostly cleanup-scope, policy-consistency, and test-coverage issues—the satisfying kind to fix because each one turns a fuzzy failure mode into a predictable contract.
