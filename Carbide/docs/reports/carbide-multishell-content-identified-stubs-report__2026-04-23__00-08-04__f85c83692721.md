# Report: content-identified virtual executable stubs for `carbide-multishell`

- Created (UTC): 2026-04-23T00:08:04Z
- Repository HEAD: 0a28ddb977c5286b608ad319f721a34b956c4703
- Status: Informational design-variant evaluation
- Audience: Carbide Contributors; future Carbide contributors working on `carbide-shell-core` and `carbide-multishell`
- Scope: Evaluate a variant where virtual executable identity is determined by stub-file content rather than by the installation path alone; compare raw-GUID, pseudo-PE, and hybrid approaches; summarize relevant external prior art
- Related code:
  - `src/Carbide/packages/carbide-shell-core/src/Apps/StubInstaller.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellSession.cs`
- Related docs:
  - [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](../proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md)
  - [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](../proposals/carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md)
- External references:
  - [Linux `execve(2)` interpreter scripts](https://man7.org/linux/man-pages/man2/execve.2.html)
  - [Linux `binfmt_misc`](https://docs.kernel.org/6.12/admin-guide/binfmt-misc.html)
  - [BusyBox multi-call binary overview](https://busybox.net/BusyBox.html)
  - [Windows AppExecutionAlias packaging extension](https://learn.microsoft.com/uk-ua/windows/apps/desktop/modernize/desktop-to-uwp-extensions)
  - [npm `package.json` `bin` field](https://docs.npmjs.com/cli/v6/configuring-npm/package-json/)

## Summary

The content-identified variant is technically viable, but it does **not** remove the need for path-based discovery, shell-specific PATH order, or collision handling for names like `find`, `sort`, and `tar`. What it changes is the second half of the problem: once the dispatcher has found a candidate file, it can derive the command identity from the file payload instead of from the file's absolute VFS path.

That trade buys one genuinely useful property: copied or renamed stubs can continue to work without being pre-registered under every destination path. Everything else is mixed. A raw GUID file is easy to parse but opaque and easy to forge. A pseudo-PE stub looks more realistic, but inside Carbide's browser/Node-hosted VFS it is mostly ceremonial complexity. The best content-based shape, if we want one at all, is a **small human-readable Carbide stub manifest** rather than either a bare GUID or a fake PE.

My recommendation is:

1. Keep the path-based registry proposed in the earlier VFS-stub proposal as the primary design.
2. If we want relocation/copy semantics later, add an **optional hybrid fallback**: known installed paths still register eagerly, but unregistered files whose content starts with a Carbide stub header can be parsed on demand and resolved by command id.
3. Do **not** make a raw GUID the public stub format, and do **not** spend effort on PE-looking stubs unless Carbide later needs to export these stubs onto a real Windows filesystem or interoperate with PE-aware tooling.

## 1. Baseline: what Carbide does today

The current shell-stub implementation is already very close to the design under discussion.

- `StubInstaller.Install(...)` creates a VFS file whose content is a small text banner: `#!carbide:<kernel.Name>\n`.
- `ShellDispatcher` ignores that content and instead remembers the owning shell in `_byStubPath`, keyed by the stub's normalized absolute VFS path.
- `MultishellSession` constructs one shared dispatcher, and each shell host registers its own names, extensions, and stub paths into that shared resolver.

So Carbide already has two distinct things:

- **discovery**: finding a candidate command by name or by path;
- **identity**: deciding which implementation that file should invoke.

Right now the identity is effectively "whatever kernel was registered for this exact path". The content is descriptive only.

The earlier VFS executable-stubs proposal extends that same model from three shell stubs (`pwsh`, `cmd`, `bash`) to a larger catalog (`robocopy`, `findstr`, `awk`, `sed`, `grep`, `tar`, and so on). The question in this report is whether the second half should change from path-based identity to content-based identity.

## 2. The design variant being evaluated

There are really three content-based sub-variants:

### 2.1 Raw GUID payload

The file contents are just a GUID, or a short opaque token that `carbide-multishell` recognizes:

```text
5f1ec7c4-4134-4b7d-8fa4-3d4e8f9478c2
```

When the dispatcher finds a file candidate, it reads the file, sees that the entire content matches a known stub id, and routes to the corresponding handler.

### 2.2 Textual Carbide manifest

The file is still tiny text, but the content is self-describing:

```text
#!carbide-exe v1
id=gnu-sed
personality=gnu
```

This is still content-identified, but it is much easier to inspect, debug, diff, and extend than a bare GUID. This report treats it as the strongest content-based option.

### 2.3 Pseudo-PE stub with embedded id

The file starts with enough DOS/PE structure to look like an `.exe`, and somewhere in a reserved section or resource it carries a GUID or other command id. Carbide would parse the PE-shaped wrapper just far enough to extract that marker and map it to a handler.

The user-visible appeal is obvious: `foo.exe` looks like an executable rather than a text file. The technical question is whether that realism buys anything material inside Carbide.

## 3. What content identity changes, and what it does not

### 3.1 What it changes

Content identity gives the stub a form of self-description.

If `/usr/bin/sed.exe` is copied to `/tmp/x.exe`, the copied file can still resolve to `gnu-sed` without a separate registry update, assuming the dispatcher is willing to inspect the file content at execution time. That is the strongest argument for this variant.

Other benefits:

- The registry can be keyed by **command id** instead of by every installed path.
- Snapshots become more self-contained if they are exported and reloaded without replaying "install stubs" logic.
- A path no longer has to be the canonical truth for which command a stub represents.

### 3.2 What it does not change

It does **not** remove path-based discovery.

Even with content-identified stubs, all of the following still remain path problems:

- shell-specific PATH defaults;
- Windows-vs-GNU collision handling (`find`, `sort`, `tar`);
- `/usr/bin` vs `/bin` vs `/Windows/System32` vs `C:\Program Files\Git\usr\bin`;
- pwsh/cmd `PATHEXT`-style suffix inference;
- explicit path invocations vs bare command-name invocations.

In other words:

- **discovery stays path-based;**
- only **implementation selection** becomes content-based.

That means the content-based variant is an additive refinement, not a replacement for the directory-root and command-search rules already defined in the VFS-stub proposal.

## 4. Evaluation criteria

For Carbide, the important criteria are:

1. Does it preserve realistic shell behavior?
2. Does it make copied/renamed stubs work naturally?
3. Is it easy for humans to inspect and debug?
4. Does it avoid accidental or malicious spoofing?
5. Does it keep the runtime small and simple in browser/Node hosts?
6. Does it preserve a plausible path to future evolution?

## 5. Comparison matrix

| Option | Copy/rename keeps working | Human-inspectable | Realistic `.exe` appearance | Extra parser complexity | Spoof resistance | Carbide fit |
|---|---|---|---|---|---|---|
| Path-registered text banner | No, unless destination path is registered | High | Low | Low | Medium: path allowlist is policy | Strong baseline |
| Raw GUID content | Yes | Low | Very low | Low | Low | Acceptable but unattractive |
| Text manifest content | Yes | High | Low | Low | Low | Best content-based option |
| Pseudo-PE with embedded GUID | Yes | Low to medium | High | Medium to high | Low | Weak cost/benefit today |
| Hybrid path + text manifest | Installed paths: yes; copied paths: yes if fallback parse is enabled | High | Low | Low to medium | Medium | Best overall if copy semantics matter |

Two rows deserve emphasis:

- The **current path-based design** is still the simplest and most honest model for Carbide's first implementation.
- The **hybrid text-manifest design** is the only content-based variant whose benefits look worth its cost.

## 6. Detailed evaluation

### 6.1 Raw GUID payload

#### Advantages

- Extremely small.
- Trivial to parse.
- Easy to make unique.
- Decouples command identity from the installation path.

#### Disadvantages

- Opaque in directory listings and file inspection.
- Poor debugging ergonomics: a user who runs `Get-Content /usr/bin/sed.exe` learns nothing useful.
- No room for forward-compatible metadata unless the format grows later anyway.
- Encourages treating the GUID as a secret, even though it is not one.

#### Important security note

A GUID is an identifier, not an authenticator.

If users can create arbitrary files in the VFS, they can also create a file containing a known GUID. Therefore raw GUID content does not meaningfully protect the runtime from spoofed stubs. At best it avoids accidental collisions.

#### Verdict

Technically workable, but a bad public format. If Carbide ever adopts content-based identification, it should skip straight past "raw GUID only" and use a textual manifest.

### 6.2 Text-manifest content

#### Advantages

- Keeps the useful property of path-independent identity.
- Remains easy to parse.
- Is self-describing when inspected by the user.
- Can carry versioning and additional metadata without format churn.
- Closest in spirit to shebang-style prior art, which has aged very well.

#### Disadvantages

- Still does not look like a Windows PE.
- Still allows spoofing if we permit arbitrary user-created stubs to auto-resolve.
- Requires the dispatcher to read candidate files when a path is not pre-registered.

#### Suggested shape

If Carbide ever uses a content-based stub, I would make the file format explicit and textual, for example:

```text
#!carbide-exe v1
id=windows-robocopy
personality=windows
display=robocopy.exe
```

That gives us:

- a stable parse marker;
- a semantic command id that can appear in logs, tests, and docs;
- room for future metadata;
- content that remains readable in snapshots.

If a GUID is still desired internally, it can exist as an additional field rather than as the entire payload.

#### Verdict

Best pure content-based design. If we go down this road at all, this is the right way to do it.

### 6.3 Pseudo-PE with embedded GUID

#### Why it is appealing

It makes the VFS look more like Windows: a file named `robocopy.exe` can actually begin with `MZ`, contain PE headers, and perhaps even reserve a custom section for a Carbide command id. That feels closer to a real executable image.

#### Why it is weaker than it first appears

Inside Carbide's current runtime model:

- the VFS is not NTFS;
- the OS loader never sees these files;
- no real process is spawned;
- no native loader compatibility is required;
- the execution engine is still `carbide-multishell`, not the host OS.

So the pseudo-PE buys mostly **aesthetic realism**, not meaningful compatibility.

#### Costs

- PE generation or templating logic.
- PE parsing logic, or at least section/resource scanning logic.
- More brittle tests.
- More complicated documentation.
- A higher chance that readers infer "these are intended to be real executables" when they are not.

#### Subtle downside

Once Carbide starts writing PE-looking files, future maintainers will reasonably ask:

- Are these valid enough for PE inspection tools?
- Are imports, subsystem flags, machine type, and section layout supposed to mean anything?
- Should copied stubs outside the VFS run on Windows?
- Should a Node host be able to export them to disk and execute them?

That is a large implied scope increase for very little present value.

#### Verdict

Not recommended for the current problem. It is the most expensive variant and the least aligned with Carbide's actual execution model.

## 7. The strongest design consequence: content identity is not enough policy

There are two distinct policy questions:

1. Which command should this file invoke?
2. Which files are allowed to act as environment-backed virtual executables?

Content-based identity only answers the first question.

If Carbide treats any file containing a recognized id as executable, then any project content can create additional command-entry points. That is not necessarily unsafe in Carbide's trust model, but it is a behavioral choice and should be explicit.

If we want tighter control, then the runtime still needs one of:

- a path allowlist;
- a directory allowlist;
- a signed or MACed manifest format;
- hidden VFS metadata separate from visible bytes.

That is why I do **not** recommend saying "content determines identity" as a complete design statement. More precisely:

- content can determine **which handler** a recognized stub maps to;
- the runtime still needs a policy for **whether this file is eligible to be treated as a stub at all**.

## 8. Prior art

### 8.1 Shebang interpreter scripts

The `execve(2)` manual describes interpreter scripts as text files whose first line is `#!interpreter [optional-arg]`; the kernel invokes the named interpreter and passes the script path and arguments to it. This is content-first dispatch, and it has three properties worth borrowing:

- the trigger is at the start of the file;
- the content is human-readable;
- the payload identifies an implementation contract rather than pretending to be a native binary.

This is the cleanest precedent for a Carbide text-manifest stub.

### 8.2 `binfmt_misc`

Linux `binfmt_misc` is even closer to the proposed variant. The kernel documentation explicitly states that it can recognize a binary type by matching bytes at the beginning of the file, with a mask and offset, and can also recognize filename extensions. That makes `binfmt_misc` a real deployed example of:

- registry-backed execution behavior;
- content-based dispatch via magic bytes;
- extension-based fallback;
- one environment-supplied implementation behind many apparent binaries.

If Carbide ever wanted a pseudo-PE-with-marker design, that would effectively be a private `binfmt_misc` scheme running inside the shell dispatcher.

### 8.3 BusyBox multi-call binary

BusyBox is strong prior art for the broader "many tools, one implementation surface" idea. Its documentation describes BusyBox as a multi-call binary: one executable provides many applets, and symlinks or hardlinks install multiple command names that all route into the same implementation image.

BusyBox is important because it validates the **runtime shape** we want: one engine backing many tools. But its selection mechanism is name- and path-driven (`argv[0]` and installed link names), not content-driven. In other words, it argues for the feasibility of a shared implementation surface, but not specifically for a GUID-in-file design.

### 8.4 Windows App Execution Aliases

Microsoft's packaging documentation describes `windows.appExecutionAlias` as an extension that lets a packaged app register an alias such as `contosoapp.exe`, so users and other processes can launch the app without specifying the full path. That is very strong prior art for "the environment supplies the implementation behind a stub-like command entry."

However, it is again mostly path/alias oriented, not content oriented.

The local machine probe I ran during this investigation makes that concrete:

- `%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe`, `winget.exe`, and `wt.exe` resolve as `Application` commands in PowerShell.
- On disk they appear as **0-byte reparse points** with Microsoft reparse tag `0x8000001b`, not as normal PE files.
- `fsutil reparsepoint query` shows package-family and target-path data inside the reparse payload.

So Windows itself already uses environment-owned command-entry placeholders whose implementation lives elsewhere. But the mechanism is metadata/path-based, not "read an embedded GUID from a fake PE".

This is the closest real Windows precedent, and it actually strengthens the case for **path or metadata registration** more than the case for PE-looking content signatures.

### 8.5 npm bin links and shims

npm is another good supporting example. The `package.json` `bin` field installs executables into the PATH. On POSIX those are typically symlinks; npm's docs also note that on Windows package executables may be installed as `.cmd` shims.

This is not quite the same as Carbide's model, because the shim normally launches a real script or runtime. Still, it shows a very common pattern:

- present a small entry point in PATH;
- keep the real implementation elsewhere;
- let the environment decide how to bridge from entry point to implementation.

Again, the important part is that the entry point need not itself contain the full implementation. That is normal.

## 9. Recommended design for Carbide

### 9.1 Recommendation

For the first serious multishell utility implementation, keep the path-based design from the existing proposal.

That design already has the right properties for Carbide's immediate needs:

- explicit install roots;
- realistic shell discovery;
- clear collision handling via PATH order;
- low implementation complexity;
- easy tests and docs.

### 9.2 If we want content-aware copies later

Add a **hybrid fallback**, not a full replacement.

Recommended behavior:

1. Known installed stub paths continue to register eagerly in a dispatcher-side registry for fast resolution.
2. When a path-like invocation or PATH search finds a file that is not in that registry, the dispatcher may read the first small prefix of the file.
3. If the file starts with a recognized Carbide stub header, the dispatcher extracts `id=` and resolves that command.
4. Otherwise the normal script/app resolution rules continue unchanged.

This keeps the first implementation simple while preserving a future path to "copied stubs still work".

### 9.3 Recommended stub format if hybrid is adopted

Use a short text manifest, not a bare GUID and not a pseudo-PE:

```text
#!carbide-exe v1
id=gnu-awk
personality=gnu
```

Optional additional fields could include:

- `display=awk.exe`
- `extensions=.exe`
- `origin=/usr/bin/awk.exe`

But the required minimum should stay tiny.

### 9.4 Recommendation on GUIDs

If GUIDs are used at all, use them as **internal serialization keys**, not as the user-facing format.

For example:

```text
#!carbide-exe v1
id=gnu-awk
guid=5f1ec7c4-4134-4b7d-8fa4-3d4e8f9478c2
```

That lets the runtime keep a collision-free opaque id if desired, while humans and tests still operate on a meaningful `id`.

### 9.5 Recommendation on pseudo-PE stubs

Defer completely unless one of these becomes a real requirement:

- exporting stubs to a real Windows filesystem;
- interop with PE-aware analysis tools;
- a host mode that actually asks the Windows loader to launch the stub;
- a product goal that explicitly values PE-like realism in directory listings over implementation simplicity.

Until then, fake PE images would mostly be decorative debt.

## 10. Concrete implications if Carbide adopts the hybrid fallback

These are the concrete feature additions I would make, and no more:

### `carbide-shell-core`

- Add a command-id registry for virtual executables.
- Keep the existing eager path registry for installed stubs.
- Add a small content-parser fast path for files that start with `#!carbide-exe `.

### `carbide-multishell`

- Install stubs using the existing explicit paths from the VFS-stub proposal.
- Write the text manifest content instead of a shell-only banner when the stub represents a utility executable.
- Continue to define shell-specific PATH defaults and collision precedence exactly as in the earlier proposal.

### Tests

- Executing an installed stub path still works.
- Copying a recognized stub file to a different directory works if content-fallback resolution is enabled.
- A random text file does not resolve as a virtual executable.
- A file with a malformed Carbide stub header does not resolve.
- Windows-vs-GNU name collisions are still governed by search path order, not by content ids alone.

## 11. Final verdict

The idea is sound, but the best version of it is narrower than the raw proposal suggests.

- **Content-based identity is useful for relocation and snapshot self-description.**
- **It does not replace path-based discovery or shell-specific command-precedence rules.**
- **A bare GUID is too opaque.**
- **A pseudo-PE is too expensive for the value it provides today.**
- **A small text manifest is the only content-based format that really fits Carbide's current host model.**

So the practical answer is:

- path-based registry first;
- optional text-manifest fallback later if we decide copied stubs should keep working;
- no fake PE work unless the runtime model changes materially.
