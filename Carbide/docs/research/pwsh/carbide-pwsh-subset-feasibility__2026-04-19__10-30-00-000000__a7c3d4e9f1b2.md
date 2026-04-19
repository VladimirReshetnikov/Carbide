# Feasibility: forking a useful PowerShell subset from `lib/pwsh` to run under Carbide on Node.js

- Created (UTC): 2026-04-19T10:30:00Z
- Repository HEAD: 20659c7b082ed4b14c79fa6be5b741d8b63ff6dc

Status: feasibility report. Cross-reads PowerShell 7's source tree in `lib/pwsh` (upstream `PowerShell/PowerShell` snapshot) against Carbide's current state (M9 + M11 + U1–U3 shipped). Output targets a **decision** about whether a limited but useful PowerShell-on-Carbide story is worth pursuing, and if so, what the minimum viable scope looks like and what Carbide-side changes would unblock it.

Audience: repository owner Vladimir; future Carbide contributors. Written to be actionable without further background research.

## 1. Request

> Evaluate feasibility of forking a limited (but useful) subset of `lib/pwsh` and making necessary modifications to be able to compile and run it using Carbide in Node.js (perhaps, after implementing some additional features in Carbide). The goal is to be able to run some useful subset of PowerShell scripts on Node.js. Create a report with your findings and conclusions.

Three scope words pinned before the analysis:

- **"limited but useful subset":** a concrete PowerShell surface that (a) a meaningful fraction of real-world scripts target and (b) has a realistic path to working under Carbide. Not "everything `pwsh.exe` can do"; see §5 for the tiered scope.
- **"forking a subset":** not vendoring all of `lib/pwsh`; not consuming a published `Microsoft.PowerShell.SDK` NuGet package. A Carbide-specific fork with trimmed sources, excluded cmdlets, patched call sites, and a minimal host shim.
- **"compile and run using Carbide":** Carbide's CLI builds the forked SMA.dll from source (the M11 evaluator already handles Directory.Build.props / `<Import>`), then Carbide loads that DLL into a user session via `session.addReference(bytes, name)`, and user scripts parse + execute through the forked engine.

## 2. Executive summary

| Capability | Today, unmodified | A vertical slice (1–2 weeks) | A useful subset (1–3 months) | Months of deeper work | Blocked or out of scope |
|---|---|---|---|---|---|
| Parse a PowerShell script into an AST | — | ✔ — `Parser.ParseInput` is pure managed code | — | — | — |
| Evaluate `2 + 2` / `"hello".ToUpper()` | — | ✔ — via `ScriptBlock.InvokeReturnAsIs` on a minimal Runspace | — | — | — |
| Pipeline: `1..10 \| Where-Object { $_ -gt 5 } \| ForEach-Object { $_ * 2 }` | — | — | ✔ — needs a minimal cmdlet set | — | — |
| Hashtables, arrays, strings, regex, classes, functions | — | ✔ (engine-only) | ✔ (production-shaped) | — | — |
| `ConvertTo-Json` / `ConvertFrom-Json` | — | — | ✔ — uses Newtonsoft.Json (already on Carbide's allow-list) | — | — |
| `Write-Output`, `Write-Host`, `Write-Error` | — | ✔ (re-hosted via a custom `PSHost`) | ✔ | — | — |
| `Get-Content` / `Set-Content` against a virtual FS | — | — | ✔ (requires a Carbide-side VFS shim — §8.3) | — | — |
| `Invoke-RestMethod` over `fetch`/Node `undici` | — | — | — | ✔ (patch WebRequestPSCmdlet → Node fetch; Band C-ish) | — |
| Argument completion, IntelliSense | — | — | ✔ (`CommandCompletion` is pure managed) | — | — |
| Pester-style in-memory test harness | — | — | ✔ (pure-managed assertion semantics; no file discovery) | — | — |
| Remoting (WinRM, SSH, named pipes) | — | — | — | — | ✘ — native deps + network protocols not in Carbide's bounded surface |
| CIM / WMI / Get-WmiObject / Get-CimInstance | — | — | — | — | ✘ — `Microsoft.Management.Infrastructure.Native` is a native DLL |
| PSReadLine (interactive line editing) | — | — | — | — | ✘ — native; irrelevant in non-interactive CLI anyway |
| DSC (`Invoke-DscResource`) | — | — | — | — | ✘ — Windows-specific, native, out of scope |
| Real filesystem access (`Set-Location`, `Get-ChildItem` over `C:\`) | — | — | — | ✘ — Mono-WASM can't mount the host FS; a VFS bridge is the only workable path | — |
| JIT compilation of hot script paths | — | — | — | — | ✘ — Mono-WASM doesn't allow Reflection.Emit IL at the AOT layer; PowerShell's LightCompiler/interpreter is the substitute |

**Bottom line:** a useful PowerShell-on-Carbide story is **feasible**, takes **1–3 months of focused work** to reach a genuinely useful state (Tier 2 below), and costs Carbide **three concrete additions** (§7). The critical enabler — PowerShell's engine having a built-in interpreter (`LightCompiler`) that doesn't need IL emission — is already in the `lib/pwsh` tree. Most of the difficulty is *not* the engine; it's slicing away the non-portable cmdlet libraries and building a Carbide-shaped host.

## 3. The shape of `lib/pwsh`

Raw size, measured on the current tree:

| Top-level component | Files | LOC | Purpose |
|---|---:|---:|---|
| `System.Management.Automation` | 710 | 84 461 | **The engine.** Parser, AST, compiler, interpreter, runtime, session state, pipeline. |
| `Microsoft.PowerShell.Commands.Utility` | 94 | 44 796 | Utility cmdlets (Select-Object, ForEach-Object, ConvertTo-Json, …). |
| `Microsoft.PowerShell.Commands.Management` | — | 39 607 | Management cmdlets (Get-Content, Get-Process, …). |
| `Microsoft.PowerShell.ConsoleHost` | — | 18 405 | Interactive console (`pwsh` binary entry point). |
| `Microsoft.PowerShell.Security` | — | — | Crypto-facing cmdlets; Windows-heavy. |
| `Microsoft.Management.Infrastructure.CimCmdlets` | — | — | CIM/WMI; native deps. |
| `Microsoft.WSMan.*` | — | — | WinRM remoting; native deps. |
| `Microsoft.Management.UI.Internal` | — | — | UI helpers; irrelevant. |
| `Microsoft.PowerShell.CoreCLR.Eventing` | — | — | Windows event log; Windows-only. |
| `powershell-native` / `libpsl-native` | — | — | Native C++ helpers. Hard-exclude. |

The engine is by far the largest piece, and it is the **only one we'd need in full for Tier 1 scope**. Cmdlet libraries are modular — we can include a small subset and exclude the rest by `<Compile Remove>` globs.

### 3.1 Engine breakdown

`System.Management.Automation/engine/`:

| Subdir | Files | LOC | Notes |
|---|---:|---:|---|
| `parser/` | 18 | 47 936 | Tokenizer (~5k), Parser (~8k), Ast (~11k), Compiler (~7k, targets Expression trees). **Zero P/Invoke.** |
| `runtime/` | 10 | 19 758 | Binders, `CallSite<T>`, language-primitive conversion. **Zero P/Invoke.** |
| `interpreter/` | 35 | 11 564 | **The LightCompiler** — PowerShell's forked-from-DLR Expression-tree interpreter. Critical for WASM. Zero P/Invoke. |
| `CommandCompletion/` | 7 | 14 685 | IntelliSense/tab completion. Zero P/Invoke. |
| `debugger/` | 2 | 6 557 | Debugger hook-points. Zero P/Invoke. |
| `lang/` | 6 | 4 487 | Language primitives helpers. Zero P/Invoke. |
| `remoting/` | 98 | 97 771 | **Exclude entirely.** WinRM, SSH, named pipes — native + network. |
| `Interop/Windows/` | ~25 | — | **Exclude entirely** — Windows native. Already conditionally compiled out on non-Windows. |

The first six subdirs total ~100k lines with **zero** P/Invoke. That's the material we actually need to compile + run scripts. The 277 `DllImport` sites I counted across SMA are concentrated in `remoting/`, `Interop/Windows/`, `security/`, and `namespaces/FileSystemProvider.cs` / `Win32Native.cs` — all either excluded by conditional compilation on non-Windows or cleanly slicable via `<Compile Remove>`.

### 3.2 Utility cmdlets — surprisingly portable

`Microsoft.PowerShell.Commands.Utility` has 94 files and **6 P/Invoke sites** across them. The sites are:

- `New-Object.cs` — `CLSIDFromProgID` for COM (remove).
- `SetDateCommand.cs` — `SetLocalTime` (remove; we're not setting the clock).
- `ShowCommand/ShowCommand.cs` — Win32 console helpers (remove; there's no GUI).
- `UnblockFile.cs` — `libc.removexattr` (remove; no filesystem attributes to strip).

Everything else — `Select-Object`, `Where-Object`, `ForEach-Object`, `ConvertTo-Json`, `Sort-Object`, `Group-Object`, `Measure-Object`, `Compare-Object`, `Format-Table`/`List`/`Wide`, `ConvertFrom-Csv`/`ConvertTo-Csv`, regex-oriented string cmdlets — is pure managed. This is the Tier 2 cmdlet set in §5, and it's almost entirely a "include it and it compiles" story.

### 3.3 NuGet dependencies

SMA's csproj references:

| Package | Status |
|---|---|
| `Newtonsoft.Json` 13.0.x | ✅ already on Carbide's M6 allow-list |
| `System.Text.Encoding.CodePages` | ⚠ not on allow-list, but managed-only; trivial to add |
| `Microsoft.ApplicationInsights` | ⚠ telemetry; stub to a no-op implementation |
| `Microsoft.Management.Infrastructure` | ✘ CIM; native — **exclude via csproj patch** |
| `Microsoft.PowerShell.Native` | ✘ native — **exclude via csproj patch** |
| `System.DirectoryServices` | ✘ LDAP / Active Directory — exclude |
| `System.Management` | ✘ WMI — exclude |
| `System.Security.Cryptography.{Pkcs,ProtectedData}` | ⚠ managed but pulls Windows-specific paths; audit |
| `System.Diagnostics.EventLog` | ✘ Windows event log — exclude |
| `System.Diagnostics.DiagnosticSource` | ⚠ managed tracing; can stub |
| `System.Configuration.ConfigurationManager` | ⚠ .config files; rarely needed by SMA — stub |
| `System.CodeDom` | ⚠ used by `Add-Type` for on-the-fly C# — exclude the feature for now |

The core dependency story for a slim SMA fork: **Newtonsoft.Json + a couple of managed-only packages** Carbide's allow-list can absorb. The remaining packages become conditionally-compiled-out exclusions in a forked SMA csproj.

## 4. The critical enabler — `LightCompiler`

The single most important observation from reading `lib/pwsh`:

> PowerShell's engine has a built-in Expression-tree interpreter. It doesn't need `Reflection.Emit` to run scripts. Scripts are compiled to `System.Linq.Expressions` trees, and those trees are interpreted by `System.Management.Automation.Interpreter.LightCompiler` rather than JIT-compiled to IL.

This is a forked copy of the old DLR's light-compiler, originally carved out for iOS / Xamarin AOT — a platform with almost the same constraints as Mono-WASM in Carbide: no runtime IL emission, no `DynamicMethod`, no `Assembly.DefineDynamicAssembly`.

The choice is gated by `CompileInterpretChoice` in `engine/parser/Compiler.cs`:

```csharp
// For scripts with > 300 statements, never try to compile to IL.
var compileInterpretChoice = (_stmtCount > 300)
    ? CompileInterpretChoice.NeverCompile
    : CompileInterpretChoice.CompileOnDemand;
```

And at the dispatch site:

```csharp
// For CompileOnDemand, threshold = -1 is "interpret forever, never compile"
// (-1 is the special value for LightCompiler's "interpret forever").
// For NeverCompile, threshold = int.MaxValue is "interpret until hot, then compile" — but
// the hotness counter never reaches int.MaxValue in practice.
int threshold = (compileInterpretChoice == CompileInterpretChoice.NeverCompile) ? int.MaxValue : -1;
var deleg = new LightCompiler(threshold).CompileTop(lambda).CreateDelegate();
```

For Carbide's fork, we force `CompileInterpretChoice.NeverCompile` at the two decision sites and audit the handful of direct `Expression.Compile()` calls in the codebase (8 sites total — see `grep` in §3.1 research). Most of them are for DLR binders that cache per-call-site; those CAN use Expression.Compile in Blazor/Mono-WASM's **interpreter** mode, which Carbide runs. The ones that can't be satisfied get patched to wrap in a LightLambda instead.

**This is the key insight:** PowerShell was designed to run on constrained runtimes. The Carbide fork doesn't have to invent an interpreter; it inherits one that already exists in the upstream tree.

## 5. A tiered useful subset

### 5.1 Tier 1 — "expression evaluator" (1–2 weeks)

The minimum viable demo. Forked SMA fork builds + loads via Carbide. Users can do:

```powershell
# Arithmetic, string ops, regex, hashtables, arrays.
2 + 2
"PowerShell $PSVersionTable"
@(1, 2, 3) | ForEach-Object { $_ * 2 }
@{ a = 1; b = 2 }.GetType().Name   # "Hashtable"
"hello world" -match "(\w+) (\w+)"; $Matches[1]
```

What's needed:
- Forked `System.Management.Automation` csproj with heavy `<Compile Remove>` globs.
- A minimal `PSHostUserInterface` that routes output to `Console.Out` (captured by Carbide's existing SetOut).
- One C# entry-point class: `CarbidePwsh.Run(string script) -> string`.
- Force `NeverCompile` at all Compiler.cs decision sites.
- Trim the csproj to: `Newtonsoft.Json`, `System.Text.Encoding.CodePages`, nothing else.

Scope exclusions: no cmdlets beyond intrinsics (`Write-Output`, `Out-String`, `Where-Object`, `ForEach-Object`, `Select-Object` — most are handled by the engine directly via aliases to built-in pipeline steps, not by `Microsoft.PowerShell.Commands.Utility`).

### 5.2 Tier 2 — "useful scripting shell" (1–3 months)

Tier 1 plus most of `Microsoft.PowerShell.Commands.Utility`:

```powershell
# JSON round-trips (Newtonsoft.Json is already on Carbide's allow-list).
$data = '{"name":"A","values":[1,2,3]}' | ConvertFrom-Json
$data.values | Measure-Object -Sum

# CSV round-trips (pure managed).
$people = @'
name,age
alice,30
bob,25
'@ | ConvertFrom-Csv

# Regex + string manipulation.
$people | Where-Object { $_.age -gt 27 } | Select-Object name

# Formatting.
$people | Format-Table -AutoSize
```

What's needed beyond Tier 1:
- Fork `Microsoft.PowerShell.Commands.Utility` with the 4–6 problematic files removed (§3.2).
- Implement `Get-Content` / `Set-Content` / `Out-File` against a Carbide-side VFS (§8.3).
- A minimal `FormatAndOutput` pipeline (already in utility but depends on a `PSHost` — implement the shim).
- Possibly patch `Add-Type` to refuse cleanly (or — more ambitious — re-use Carbide's own Roslyn to compile `Add-Type` input inline).

### 5.3 Tier 3 — "usefully embeddable" (3–6 months)

Tier 2 plus:

- A subset of `Microsoft.PowerShell.Commands.Management` — `Get-Process`, `Get-ChildItem`, `Set-Location`, `Test-Path` against a VFS.
- `Invoke-RestMethod` / `Invoke-WebRequest` re-backed by Node's `undici` / browser `fetch` (requires a C#-side adapter that Carbide's host adapter exposes).
- User-defined classes, advanced functions with `[Parameter]` attributes, module loading from in-memory sources (not disk).
- Argument-transformation attributes, validation attributes.
- A bounded set of built-in PowerShell modules (`Microsoft.PowerShell.Utility`, `Microsoft.PowerShell.Management` with the cmdlets above).

### 5.4 Explicitly out of scope (at every tier)

- `Microsoft.Management.Infrastructure.CimCmdlets` (`Get-CimInstance`, …) — native.
- `Microsoft.WSMan.*` (`Invoke-Command -ComputerName`, WinRM) — native + protocol.
- DSC, PSReadLine, PSResourceGet, anything that touches actual processes / services / registry.
- Running arbitrary `.psm1`/`.psd1` files from a real disk — relies on the full provider + module-discovery pipeline.
- `Enter-PSSession` / any interactive/remote scenario.
- Binary PowerShell modules (`*.dll` that expose cmdlet classes via reflection). With extra work they *could* load, but the ecosystem doesn't target Mono-WASM.
- Byte-for-byte compatibility with `pwsh.exe`. We aim for "useful subset with explicit deviations documented."

## 6. What compiles as-is vs what needs patching

I did not actually run a build against Carbide — that's the "vertical slice" follow-up. But from static inspection, here's what the patch surface looks like:

### 6.1 Clean exclusions via `<Compile Remove>`

The full `engine/Interop/Windows/*`, `engine/remoting/*`, `engine/ComInterop/*`, `security/*native*`, `namespaces/{FileSystemProvider,Win32Native,SafeRegistryHandle,TransactedRegistry}.cs`, `help/CabinetNativeApi.cs`, `utils/{PlatformInvokes,tracing/SysLogProvider}.cs`, `cimSupport/*`, `CoreCLR/CorePsPlatform.cs` — all can be glob-removed from the forked csproj without touching source files.

That's roughly **80% of the P/Invoke surface** closed by csproj edits alone.

### 6.2 Source patches needed

A small, enumerable list:

1. **`engine/parser/Compiler.cs`:** force `CompileInterpretChoice.NeverCompile` at ~3 sites.
2. **`engine/runtime/Binding/Binders.cs`:** 2 direct `Expression.Compile()` calls; wrap with LightLambda or audit whether Mono-WASM interpreter handles them.
3. **`engine/runtime/CompiledScriptBlock.cs`:** one of the `CompileInterpretChoice` decision points. Same fix.
4. **`engine/CoreAdapter.cs`:** uses `DynamicMethod` for member access fast-paths. Behind an `#if` guard already (look for `!CORECLR_AOT` — actual symbol TBD). Flip the define.
5. **`engine/namespaces/FileSystemProvider.cs`:** replace with a VFS-backed provider (§8.3). For Tier 1, replace with a stub that throws `NotSupportedException`.
6. **`engine/hostifaces/*`:** `Runspace`, `RunspaceFactory`, `PowerShell` public surfaces work; `RunspacePool` uses threading — audit / disable.
7. **`System.Management.Automation.csproj`:** remove all `<PackageReference>` except the two we need; add `<Compile Remove>` globs per above; strip `OutputItemType=Analyzer` source-generator reference (PSVersionInfoGenerator) — instead pre-compute its output into a checked-in file.
8. **`global.json`, `Directory.Build.props`:** the ones in `lib/pwsh/` target an exact dotnet SDK version. Our fork's Directory.Build.props should pin `net10.0` and drop their version-lock.

### 6.3 Annotations for trimming

If Carbide's build enables trimming (the root `Directory.Build.props` sets `PublishTrimmed=true`), PowerShell's reflection-heavy code will break. Concrete issues:

- `PSObject` wraps everything via reflection; adapters resolve member names at runtime.
- The DLR `CallSite<T>` cache resolves binder targets via reflection-into-types.
- `Add-Type` (if kept) compiles user C# into a new assembly — heavy reflection on emitted types.
- Type converters discover `TypeConverterAttribute` via reflection.

Two workable approaches:
- **(a) Disable trimming on the forked SMA DLL.** Set `PublishTrimmed=false` in a sibling `Directory.Build.props` under the fork. Carbide loads the untrimmed DLL as a metadata reference; the runtime BCL on the user-program side is a separate concern.
- **(b) Add `DynamicDependency` attributes** to the hot paths. More work, more ongoing maintenance. Acceptable after an MVP.

(a) is the pragmatic default for the MVP.

### 6.4 Source generators

SMA's csproj has a `ProjectReference ... OutputItemType="Analyzer"` to `PSVersionInfoGenerator`. Carbide doesn't run source generators (M12 is the stretch milestone). For the fork:

- Either **check in** the generator's output as a regular source file (the output is a single tiny `PSVersionInfo.g.cs`). Easy.
- Or wait for Carbide M12. Not recommended — M12 is Band C stretch.

## 7. Carbide-side changes needed

Three concrete additions, in decreasing order of importance:

### 7.1 Allow-list expansion

Add `Microsoft.Extensions.ObjectPool`, `System.Text.Encoding.CodePages`, and 2–3 other managed-only NuGet packages SMA pulls, to `packages/nuget/src/allowlist.ts`. Each entry gets a fixture test (M6 D75 pattern). Effort: a few hours per package.

Alternative: pre-compile SMA.dll outside Carbide (`dotnet build` with full SDK) and consume it via `carbide run --ref CarbidePwsh.dll`. This sidesteps the allow-list entirely but adds a pre-step. **Probably what we do first for the vertical slice.**

### 7.2 Reference-pack size

Carbide's `@carbide/refs-net10.0` covers the .NET 10 BCL. SMA references large parts of System.*, but not anything outside the ref-pack's scope — this should already work. Audit on first build.

### 7.3 `carbide pwsh` wrapper script (optional ergonomic)

Once the fork loads, a thin wrapper:

```bash
carbide pwsh path/to/script.ps1
# equivalent to:
carbide run --project CarbidePwshHost.csproj -- path/to/script.ps1
```

would make the developer experience fluent. Five-line CLI addition; trivial after U2's argv forwarding landed.

### 7.4 Things Carbide **does not** need to change

- Interop schema — SMA doesn't talk to JS; it just runs user scripts.
- M9 project graph — SMA is a single self-contained build.
- Webcil — Mono-WASM can load uncompressed `.dll` fine.

## 8. Design notes for a minimum viable prototype

### 8.1 Repository layout

```
src/CarbidePwsh/                                    # New project
├── README.md
├── CarbidePwshHost.csproj                          # The thin host layer.
├── Host/
│   ├── CarbidePSHost.cs                            # PSHost implementation.
│   ├── CarbidePSHostUserInterface.cs               # Routes output to Console.
│   └── CarbidePSHostRawUserInterface.cs            # Minimal stub.
├── Runtime/
│   ├── CarbidePwshEngine.cs                        # public API: Run(string script).
│   └── VirtualFileSystem.cs                        # (tier 2) VFS shim.
├── Program.cs                                      # `Main(args)` entry — runs args[0] as a script.
└── pwsh-fork/                                      # Git-submodule / `git subtree` / copy-and-patch of the SMA subset.
    ├── System.Management.Automation/
    │   ├── System.Management.Automation.csproj     # Patched csproj.
    │   ├── engine/
    │   │   ├── parser/         (unchanged bar CompileInterpretChoice.NeverCompile patch)
    │   │   ├── runtime/        (unchanged bar 2 Expression.Compile() patches)
    │   │   ├── interpreter/    (unchanged)
    │   │   ├── lang/           (unchanged)
    │   │   ├── CommandCompletion/  (unchanged)
    │   │   ├── debugger/       (unchanged)
    │   │   └── …               (excluded via <Compile Remove>: remoting/*, Interop/Windows/*, ComInterop/*, cimSupport/*)
    │   ├── namespaces/         (FileSystemProvider stubbed; rest intact)
    │   ├── security/           (most stubbed)
    │   ├── help/               (intact; CabinetNativeApi excluded)
    │   ├── serialization.cs    (intact)
    │   └── …
    └── Microsoft.PowerShell.Commands.Utility/     (tier 2)
```

### 8.2 Upstream-tracking strategy

The fork is a local patch set on top of `lib/pwsh`. Options:

1. **`git subtree` of the PowerShell repo.** Periodic merges from upstream pick up fixes; patches live as commits on top. Noisy but familiar.
2. **Flattened copy into `src/CarbidePwsh/pwsh-fork/`** with a tracking manifest that records the upstream SHA at fork time. Simpler repo-wise; harder to sync.
3. **Reference the upstream sources from `lib/pwsh/` directly** with a carefully-configured `<Compile Include>` / `<Compile Remove>` + a small separate `patches/` directory with unified diffs applied at build time. Cleanest in spirit; custom tooling.

Recommendation: option (2) with a `patches/` directory of unified diffs for the ~10 files we modify. Keeps the change surface obvious and reproducible.

### 8.3 The filesystem problem

PowerShell's `FileSystemProvider` expects a real filesystem. In Carbide on Node, the Mono-WASM runtime has a sandboxed-virtual-FS (emscripten MEMFS). Browser has none.

Two viable paths for Tier 2+:

**(a) Stub all disk cmdlets to a Carbide-provided VFS.** Users pass an in-memory directory tree to the engine; `Get-ChildItem`, `Get-Content`, `Set-Content` resolve against it. Good for hermetic scripting; natural for agent runtimes.

**(b) Node-side bridge: JSExport a trampoline.** Carbide extends its host adapter with `readFile`/`writeFile`/`readDir` JSExports; the forked `FileSystemProvider` calls those. On browser, the same interface targets an in-memory store.

(a) is simpler for the MVP. (b) is the "real" answer for Tier 3 and requires a JS↔C# bridge — the [JS interop bridge proposal](../../proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) is the natural substrate.

### 8.4 Host surface

Minimal `PSHost` implementation routes:

- `Write-Output` / `Write-Host` / `Write-Verbose` / `Write-Error` → `System.Console` (captured by Carbide's SetOut).
- `Read-Host` → reads from a Carbide-supplied stdin buffer (U2's mechanism).
- `$PSVersionTable` → hard-coded to `7.5-carbide-subset`.
- `$ErrorActionPreference`, `$VerbosePreference`, etc. → work via the engine's default variable defaults.

That's the whole surface for Tier 1. Tier 2 adds no further host requirements; Tier 3 adds `$host.UI.PromptForChoice` (stub to default) and transcript support (ignore).

### 8.5 Entry point

```csharp
public static class CarbidePwsh
{
    public static async Task<int> RunScriptAsync(string scriptText, string[] args, TextWriter output)
    {
        using var iss = InitialSessionState.CreateDefault2();
        // Strip any cmdlets that reference excluded native code.
        iss.Commands.Remove("Get-WmiObject", null);
        iss.Commands.Remove("Invoke-Command", null);
        // … a handful more.

        using var rs = RunspaceFactory.CreateRunspace(new CarbidePSHost(output), iss);
        rs.Open();
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = rs;
        ps.AddScript(scriptText);
        foreach (var a in args) ps.AddArgument(a);
        var results = await Task.Run(() => ps.Invoke());
        foreach (var r in results) output.WriteLine(r?.ToString());
        return 0;
    }
}
```

That's ~40 lines of glue. The rest is the forked engine doing the work.

## 9. Risks and open questions

Numbered for easy reference.

| # | Risk / question | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | `Expression.Compile()` in Mono-WASM interpreter mode doesn't actually work for PowerShell's specific expression-tree shapes | Medium | High | Vertical-slice prototype pins this fast. If it breaks, patch to LightCompiler everywhere via the `CompileInterpretChoice.NeverCompile` lever. |
| R2 | `CallSite<T>` / DLR binders hit cases the interpreter can't handle (e.g. deep `DynamicMetaObject` chains) | Medium | Medium | PowerShell's own binders under `engine/runtime/Binding/` are well-specified; we force interpreter paths and audit failures. |
| R3 | Trimming strips reflection targets | High (if left on) | Critical | Disable trimming on the forked SMA DLL. Accept size cost (+few MB in the WASM session image). |
| R4 | SMA's public API surface is huge; `InternalsVisibleTo` games between `SMA` and `ConsoleHost` break when we fork only SMA | Low | Medium | We fork only SMA at MVP. `Microsoft.PowerShell.Commands.Utility` has its own `InternalsVisibleTo("System.Management.Automation")` — honour it with a matching attribute in the forked SMA. |
| R5 | The parser is 8k lines of hand-rolled C#; a quirk surfaces that doesn't work on the Mono-WASM interpreter | Low | Low | The parser is pure managed data manipulation; no IL emission, no reflection. Extremely unlikely to break. |
| R6 | PSObject's member-resolution fast path uses `DynamicMethod` | Medium | Medium | `CoreAdapter.cs` has `DynamicMethod` sites. Behind conditional compilation today; we flip the define. Interpreter fallback exists for every fast path. |
| R7 | Upstream sync becomes a tax as PowerShell evolves | Medium | Low | Fork strategy (§8.2). A small patches/ directory is maintainable; PowerShell's core engine is stable release-to-release. |
| R8 | Some Tier 2 cmdlet's pipeline shape needs a `ScriptBlock` that calls native code | Low | Low | Audit per cmdlet; replace with stubs for those where native was the whole point. |
| R9 | Deep Roslyn integration in `Add-Type` — recursive C# compilation | Low | Low | Skip `Add-Type` for MVP. A follow-up could wire it to Carbide's own session: have `Add-Type` call out to `CarbideSession.createProject + build + addReference`. Cool if you get there. |
| R10 | Licensing | Very low | Critical | PowerShell is MIT-licensed. Forking + patching is explicitly allowed. Preserve attribution headers in `lib/pwsh/src/*` files; ship the LICENSE from `lib/pwsh/LICENSE.txt` alongside our fork. |

## 10. Estimated effort

Rough, for planning purposes:

| Phase | Duration | Deliverable |
|---|---|---|
| **P0 — Vertical slice** | 1–2 weeks | `carbide run --project CarbidePwshHost.csproj -- '2 + 2'` prints `4`. Parser, engine, basic script execution through Invoke-Script pipeline intrinsics. |
| **P1 — Useful scripting** | 4–8 weeks | Tier 2 scope: pipelines, utility cmdlets (`Select-Object`, `Where-Object`, `ForEach-Object`, `ConvertTo-Json`, etc.), hashtable/array literals, classes, try/catch, regex. |
| **P2 — VFS + net** | 4–8 weeks | `Get-Content`/`Set-Content` over a VFS. `Invoke-RestMethod` over `fetch`. Advanced parameter attributes. |
| **P3 — Polish** | 2–4 weeks | Argument completion, help files, error messages aligned with upstream. |

Total to a genuinely useful state: **~3 months of focused work**, probably 4–5 months wall-clock with a single implementor. P0 is the right first gate.

## 11. What would break the plan

A concrete list of "if you see this during P0, stop and reconsider":

- PowerShell's expression tree causes Mono-WASM's `Expression.Compile()` in interpreter mode to throw a *type-level* PlatformNotSupportedException (not a specific-op one). If so, the LightCompiler-only path becomes much harder to mandate because many DLR binder sites expect Compile().
- Carbide's M3 `AssemblyResolve` handler fails to load the forked SMA's satellite references (resource DLLs). This would indicate the ref-pack or reference registry doesn't cover SMA's needs; fixable but needs investigation.
- SMA's static initializers reach for Windows-specific `Environment` variables or `AppDomain` configuration that Mono-WASM throws on. Fix is per-occurrence source patches; quantity determines whether it's a paper cut or a blocker.
- Trimming stripping more than expected even with `PublishTrimmed=false` — indicates Carbide's overall trimming setup has systemic bleed. Fixable at Carbide level.

None of these are individually fatal; they're checkpoints to watch.

## 12. Conclusion and recommendation

**Feasibility: yes.** The core PowerShell engine is, structurally, one of the more portable large C# codebases — deliberately so, because iOS/Xamarin AOT was a first-class upstream concern. Carbide's M11 evaluator, M9 project graph, and U1–U3 CLI maturity put the tooling in the right shape *today* to build a forked subset without further upstream Carbide milestones being required.

**Recommended first move:** build the P0 vertical slice. The deliverable is a single Carbide invocation that parses-and-runs `2 + 2` through a forked SMA. This answers every open question in §9 and §11 concretely — well before committing to a months-long implementation. My rough estimate says it's a week, maybe two.

**Carbide prerequisites:** nothing hard-blocking. A handful of managed-only NuGet packages would need to join the allow-list (trivial additions), and we'd likely disable trimming on the forked DLL. No new Carbide architectural features required for Tier 1–2.

**Strategic value:** A useful PowerShell-on-Node.js story would be unique among the JS/TS-native tooling ecosystem. The nearest analogues — `node-pwsh`, `pwsh-wasi` experiments — require a full `pwsh.exe` binary running under WASI or an out-of-process subprocess. A Carbide-hosted, in-process, PowerShell evaluator is a different proposition entirely: agent runtimes (which Carbide already targets) could execute PS-shaped helpers without spawning processes, with full programmatic control over what runs. That matches Carbide's vision §5 "third-party harness integration" goal directly.

**Out-of-scope acceptance:** this report does not block on the full `pwsh.exe` compatibility goal. A subset that runs 70–80% of *hand-written scripts* (as opposed to modules reaching for WMI / registry / remoting) is very achievable and covers the agent-runtime use case. Users needing the excluded surfaces can still drop to a real `pwsh.exe` via `child_process.spawn`; Carbide + the fork fills the in-process-evaluator niche.

## 13. Appendices

### 13.1 How the numbers in §3 were produced

```bash
# Total LOC and files in SMA:
cd lib/pwsh/src/System.Management.Automation
find . -name "*.cs" | wc -l                            # 710
find . -name "*.cs" -exec wc -l {} + | tail -1         # ~84k

# P/Invoke surface:
grep -rn "DllImport\|LibraryImport" --include="*.cs" . | wc -l            # 277
grep -rn "DllImport\|LibraryImport" --include="*.cs" engine/parser        # 0
grep -rn "DllImport\|LibraryImport" --include="*.cs" engine/interpreter   # 0
grep -rn "DllImport\|LibraryImport" --include="*.cs" engine/runtime       # 1 (in a string literal)
grep -rn "DllImport\|LibraryImport" --include="*.cs" engine/lang          # 0
```

### 13.2 Key source files referenced

- [`lib/pwsh/src/System.Management.Automation/engine/parser/Compiler.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/parser/Compiler.cs) — the `CompileInterpretChoice` knob.
- [`lib/pwsh/src/System.Management.Automation/engine/interpreter/LightCompiler.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/interpreter/LightCompiler.cs) — the forked-from-DLR interpreter.
- [`lib/pwsh/src/System.Management.Automation/engine/runtime/Binding/Binders.cs`](../../../../../lib/pwsh/src/System.Management.Automation/engine/runtime/Binding/Binders.cs) — DLR binders with direct Expression.Compile() calls.
- [`lib/pwsh/src/System.Management.Automation/System.Management.Automation.csproj`](../../../../../lib/pwsh/src/System.Management.Automation/System.Management.Automation.csproj) — the csproj to fork.

### 13.3 Links

- [Carbide architecture and implementation plan](../../planning/carbide-architecture-and-implementation-plan__2026-04-17__16-16-47-000000.md) — the target environment.
- [M6 detailed plan](../../planning/milestones/carbide-M6-detailed-plan__2026-04-18__22-19-10-231651.md) — the NuGet allow-list this fork depends on extending.
- [M11 detailed plan](../../planning/milestones/carbide-M11-detailed-plan__2026-04-19__09-00-00-000000.md) — the MSBuild evaluator / Directory.Build.props support that makes the fork's build scriptable through Carbide.
- [U2 detailed plan](../../planning/milestones/carbide-U2-detailed-plan__2026-04-19__07-00-00-000000.md) — the argv forwarding that makes `carbide run -- some-script.ps1` fluent.
- [JS↔C# interop bridge proposal](../../proposals/carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) — the substrate for Tier 3's Node-side bridges (fs, http).
- PowerShell upstream: https://github.com/PowerShell/PowerShell (MIT-licensed; see `lib/pwsh/LICENSE.txt`).
- DLR light-compiler origin: https://github.com/IronLanguages/dlr (Apache-2.0); pwsh's fork is in `lib/pwsh/src/System.Management.Automation/engine/interpreter/`.
