# Proposal: `python.exe` and `perl.exe` VFS stubs for polyglot scripting

- Created (UTC): 2026-04-25T01:45:48Z
- Repository HEAD: 9259b98cb7d6e14a3546cc90bae3a987b13825f7
- Status: Draft
- Audience: Carbide Contributors; Carbide shell maintainers; agents implementing shell-hosted language compatibility
- Scope: Add virtual executable stubs for `python.exe` and `perl.exe`, plus bounded shell-implemented language runtimes that cover the common glue-script and polyglot-script uses of Python and Perl.
- Related code:
  - `src/Carbide/packages/carbide-multishell/src/VirtualExecutableCatalog.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Core.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/VirtualExecutable.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/carbide-multishell-tests/VirtualExecutableTests.cs`
  - `src/Carbide/packages/carbide-shell-core/browser-shell-demo.mjs`
  - `src/Carbide/packages/carbide-pwsh/src/CarbidePwsh.csproj`
- Related docs:
  - `src/Carbide/docs/proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md`
  - `src/Carbide/docs/proposals/carbide-cscript-vfs-stub-proposal__2026-04-25__01-27-24-221525__eedac3f4e353.md`
  - `src/Carbide/docs/implementation/carbide-multishell-vfs-executables-implementation-plan__2026-04-23__00-04-38-000000__16b5b67bb710.md`
- External references:
  - Python command-line interface documentation: `https://docs.python.org/3/using/cmdline.html`
  - Perl command-line switches documentation: `https://perldoc.perl.org/perlrun`

## Summary

Add `python.exe` and `perl.exe` to Carbide's virtual executable catalog as language-host stubs. Unlike `grep.exe`, `sort.exe`, or `findstr.exe`, these stubs are not simple filters. They are executable entry points into bounded, shell-hosted language runtimes with VFS-only file access, Carbide environment variables, deterministic process semantics, and no host-process escape hatch.

The goal is not to ship full CPython or full Perl. The goal is to cover the high-value compatibility band found in polyglot shell scripts:

- Small Python scripts used for JSON, path, text, argument, and filesystem glue.
- Python one-liners invoked with `python -c`.
- Bounded `python -m` utility invocations for modules we explicitly implement.
- Perl one-liners, especially `-ne`, `-pe`, regex substitution, and line-oriented filters.
- Small Perl scripts used for historical configure/build/test glue.
- Shebang-dispatched scripts such as `#!/usr/bin/env python3`, `#!/usr/bin/python`, `#!/usr/bin/perl`, and `#!/usr/bin/env perl`.

This proposal recommends adding a new virtual executable personality for language runtimes, registering cross-shell search aliases for Python and Perl, and implementing language subsets that are honest, useful, and aggressively sandboxed.

## Why this belongs in Carbide

Carbide's shell story is becoming a single public endpoint with nested shell support and a shared virtual executable catalog. That creates a natural next layer: scripts that expect more than shell syntax but less than a full OS process graph.

Python and Perl are disproportionately common in this layer. They appear in build scripts, configure scripts, package bootstrap files, Git hooks, test harnesses, and polyglot shell fragments. Many uses are not interested in the full runtime. They need:

- Parse command-line flags in a predictable way.
- Read stdin and files.
- Write stdout and stderr.
- Transform lines with regexes.
- Parse or emit JSON.
- Inspect `argv`, environment variables, and the current directory.
- Traverse the VFS.
- Exit with a meaningful code.

That is a tractable compatibility target for Carbide. It also keeps the implementation aligned with the current architecture: one browser-loadable endpoint, no native process spawning, and a catalog of executable paths available from every shell flavor.

## Non-goals

This proposal does not require:

- Embedding CPython, PyPy, Perl, Strawberry Perl, ActivePerl, or a WebAssembly build of any full interpreter.
- Implementing `pip`, `venv`, `site-packages`, CPAN, XS modules, Python C extensions, Perl XS extensions, or native dynamic loading.
- Providing unrestricted host filesystem, registry, network, process, signal, terminal, socket, multiprocessing, or thread behavior.
- Matching all diagnostics, object model details, parser corner cases, warning text, encoding behavior, or optimization flags of real Python and Perl.
- Supporting arbitrary third-party Python or Perl libraries.
- Supporting interactive Python or Perl REPL parity in the first slice.

The stubs should be compatible enough for common script glue. They should fail loudly and specifically outside the supported subset.

## Current architecture fit

`VirtualExecutableCatalog` currently registers ordinary utility executables and Windows command aliases. The dispatcher resolves catalog definitions by shell-aware PATH roots and invokes `MultishellVirtualExecutableHandler` by `CommandId`.

Python and Perl can use that same dispatch path, but they should not be modeled as GNU tools. A utility personality suggests filter-like executable behavior. Python and Perl need runtime-specific command-line parsing, script loading, import/module state, and language diagnostics.

Recommended shell-core addition:

```csharp
public enum VirtualExecutablePersonality
{
    Shell,
    Gnu,
    Windows,
    Language
}
```

`Language` would be a descriptive personality only. Resolution rules can stay path-based. The main value is avoiding semantic leakage in tests, metadata, diagnostics, and future shell UX.

If we choose not to add a personality immediately, the first implementation can register these definitions with the existing closest personality and leave an explicit TODO. That is a less clean design, and this proposal recommends taking the small enum change now.

## Catalog shape

Register two language runtimes:

- `python` with command id `python`.
- `perl` with command id `perl`.

Recommended Python search names:

- `python`
- `python.exe`
- `python3`
- `python3.exe`

Recommended Perl search names:

- `perl`
- `perl.exe`

Deferred Python names:

- `py`
- `py.exe`

The Windows `py.exe` launcher has separate version-selection semantics. It should be a later compatibility layer rather than a silent alias to `python`.

Recommended stub paths:

- `/usr/bin/python`
- `/usr/bin/python.exe`
- `/usr/bin/python3`
- `/usr/bin/python3.exe`
- `/bin/python`
- `/bin/python.exe`
- `/bin/python3`
- `/bin/python3.exe`
- `/Program Files/Git/usr/bin/python`
- `/Program Files/Git/usr/bin/python.exe`
- `/Program Files/Git/usr/bin/python3`
- `/Program Files/Git/usr/bin/python3.exe`
- `/usr/bin/perl`
- `/usr/bin/perl.exe`
- `/bin/perl`
- `/bin/perl.exe`
- `/Program Files/Git/usr/bin/perl`
- `/Program Files/Git/usr/bin/perl.exe`

Do not register `/Windows/System32/python.exe` or `/Windows/System32/perl.exe` by default. Python and Perl are not Windows system binaries, and putting them in System32 makes command discovery misleading. The current shell resolver already searches `/usr/bin` and `/bin` from `cmd`, `pwsh`, and `bash`, so `python`, `python.exe`, `perl`, and `perl.exe` remain callable from every shell flavor.

If we later need stronger Windows compatibility for scripts that explicitly invoke `C:\Windows\System32\python.exe`, add those paths as an opt-in compatibility layer with clear diagnostics. They should not be the canonical locations.

## Command-line contract for Python

The first implementation should support these invocation forms:

- `python script.py [args...]`
- `python3 script.py [args...]`
- `python.exe script.py [args...]`
- `python -c "code" [args...]`
- `python -m module [args...]` for an allow-listed module set.
- `python - [args...]` to read the program from stdin.
- `python -V`
- `python --version`
- `python -h`
- `python --help`

Compatibility flags that should be accepted early:

- `-u`: run unbuffered. Carbide can treat this as an output-flush hint or no-op because shell output is already captured.
- `-S`: skip `site`. Accepted as a no-op if there is no `site` bootstrap.
- `-B`: do not write `.pyc` files. Accepted as a no-op.
- `-E`: ignore Python environment variables. Supported by suppressing `PYTHON*` variables from runtime configuration.
- `-I`: isolated mode. Supported as a stricter version of `-E` plus no user paths.
- `-O` and `-OO`: accepted as parse-level flags, with `__debug__` behavior decided explicitly.
- `-q`: quiet startup. Accepted as a no-op while no REPL banner exists.

Flags that should fail explicitly until implemented:

- `-i`: interactive after script execution.
- `-X`: implementation-specific options.
- `-W`: warning filter control, except possibly a small accepted subset.
- `--check-hash-based-pycs`, `--pycache-prefix`, and implementation-specific cache controls.

The stub should set:

- `sys.argv` according to Python's command-line rules for the supported forms.
- `sys.executable` to the resolved virtual executable path.
- `sys.path` from the script directory, `PYTHONPATH` unless isolated, and a virtual standard-library root.
- `sys.stdin`, `sys.stdout`, and `sys.stderr` to Carbide streams.
- `sys.exit()` to the virtual process exit code.

## Python language subset

The useful first slice should include:

- Literals: `None`, booleans, integers, floats, strings, bytes as a later optional addition, lists, tuples, dictionaries, and sets.
- Expressions: arithmetic, comparisons, boolean operators, indexing, slicing, attribute access, calls, comprehensions in a bounded subset, f-strings, and ternary expressions.
- Statements: assignment, augmented assignment, `if`, `elif`, `else`, `while`, `for`, `break`, `continue`, `pass`, `return`, `raise`, `try`, `except`, `finally`, `with`, `import`, and `from ... import ...`.
- Definitions: functions, default arguments, keyword arguments, `*args`, `**kwargs`, and simple classes if needed for module compatibility.
- Builtins: `print`, `input`, `len`, `range`, `enumerate`, `zip`, `map`, `filter`, `sum`, `min`, `max`, `sorted`, `reversed`, `any`, `all`, `str`, `int`, `float`, `bool`, `list`, `tuple`, `dict`, `set`, `object`, `isinstance`, `hasattr`, `getattr`, `setattr`, `open`, `repr`, `format`, `ord`, `chr`, `abs`, `round`, `exit`, and `quit`.
- Exceptions: the common built-in exception hierarchy needed by ordinary scripts.

The most valuable standard-library modules are:

- `sys`
- `os`
- `os.path`
- `pathlib`
- `json`
- `re`
- `argparse`
- `glob`
- `fnmatch`
- `textwrap`
- `csv`
- `collections`
- `itertools`
- `math`
- `statistics`
- `datetime`
- `time`
- `shutil`
- `tempfile`
- `subprocess`

Module behavior should stay VFS-bound. For example, `os.listdir`, `pathlib.Path.iterdir`, `glob.glob`, `shutil.copyfile`, and `open` should operate on Carbide's VFS. Environment access should read and write the Carbide environment. `subprocess` should be a Carbide dispatcher facade, not a host-process facade.

Recommended first `python -m` modules:

- `json.tool`
- `compileall` as an accepted no-op or syntax-check facade only if useful.
- `argparse` demonstrations are not needed as modules.

`subprocess` deserves special caution. A useful subset can support `run`, `check_call`, `check_output`, and `Popen` only when the requested executable resolves through `ShellDispatcher` and the communication pattern is simple. Unsupported streaming, background process, signal, and handle inheritance behavior should fail explicitly.

## Command-line contract for Perl

The first implementation should support these invocation forms:

- `perl script.pl [args...]`
- `perl.exe script.pl [args...]`
- `perl -e "code" [args...]`
- `perl -ne "code" [files...]`
- `perl -pe "code" [files...]`
- `perl -ane "code" [files...]`
- `perl -Fpattern -ane "code" [files...]`
- `perl -c script.pl`
- `perl -v`
- `perl --help` as a Carbide-specific help page.

Compatibility switches that should be accepted early:

- `-0[octal]` for record separator control, at least the common paragraph and null-separated cases.
- `-a` autosplit mode for `-n` and `-p`.
- `-c` syntax check.
- `-e` program text.
- `-F` autosplit pattern.
- `-I` include path.
- `-l` line-ending processing.
- `-Mmodule` and `-mmodule` for allow-listed modules.
- `-n` implicit input loop.
- `-p` implicit input loop plus print.
- `-s` rudimentary switch parsing into scalar variables if needed by legacy scripts.
- `-w` warnings.

Switches that should fail explicitly until implemented:

- `-d` debugger.
- `-T` taint mode.
- `-x` extract script from text.
- `-S` PATH-based script search, unless dispatcher integration makes it cheap.
- `-i` in-place editing, because it mutates files and has backup-suffix edge cases.

Perl one-liners are more important than broad Perl-the-language support. A surprisingly large amount of polyglot glue uses Perl as a regex-capable line transformer. `-ne` and `-pe` should be high priority.

## Perl language subset

The useful first slice should include:

- Scalars, arrays, hashes, references only if required by modules, and the default variable `$_`.
- `@ARGV`, `%ENV`, `$0`, `$.`, `$/`, `$\`, `$,`, `$?`, and common regex capture variables.
- String, numeric, and boolean operators used in common scripts.
- Regex match, substitution, transliteration if practical, capture groups, global matches, and common flags.
- Statements: assignment, `if`, `elsif`, `else`, `unless`, `while`, `for`, `foreach`, `last`, `next`, `redo` if practical, `sub`, `return`, `my`, `our`, `use`, `require`, `print`, `say`, `die`, and `warn`.
- Built-ins: `chomp`, `chop`, `split`, `join`, `push`, `pop`, `shift`, `unshift`, `keys`, `values`, `exists`, `delete`, `length`, `substr`, `index`, `rindex`, `lc`, `uc`, `lcfirst`, `ucfirst`, `sprintf`, `printf`, `open`, `close`, `readline`, `stat` in a bounded form, `glob`, `sort`, `reverse`, `map`, and `grep`.
- Filehandles: `STDIN`, `STDOUT`, `STDERR`, lexical filehandles if feasible, and a simple open-mode matrix for read, write, append, and pipe-open rejection.

The most valuable module subset is:

- `strict`
- `warnings`
- `Getopt::Long`
- `JSON::PP`
- `File::Basename`
- `File::Spec`
- `Cwd`
- `File::Path`
- `File::Copy`
- `FindBin`
- `POSIX` in a small allow-listed subset

`JSON::PP` may look optional, but it is a practical compatibility multiplier for build/test glue. If implementing it directly is too much, it can be backed by the same JSON value layer used by Python's `json` module.

## Polyglot script integration

Executable stubs alone cover explicit calls such as `python helper.py` and `perl -ne ...`. Polyglot scripting often relies on script dispatch. The implementation should therefore include or plan for three dispatcher improvements.

First, support shebang dispatch for executable VFS files. The dispatcher should inspect the first line of a script and resolve:

```text
#!/usr/bin/env python3
#!/usr/bin/env python
#!/usr/bin/python3
#!/usr/bin/python
#!/usr/bin/perl
#!/usr/bin/env perl
```

Second, support extension dispatch when the shell tries to execute a file directly:

- `.py` resolves to `python`.
- `.pl` resolves to `perl`.
- `.pm` should not be executable by default, but can participate in Perl module loading.
- `.t` can optionally resolve to `perl` when test-harness compatibility becomes relevant.

Third, make `/usr/bin/env` and `env.exe` cooperate with the language catalog. If `env python3 script.py` or `env perl script.pl` resolves through the same registry, shebang support becomes much less special.

These integrations should still respect shell semantics. For example, PowerShell should not treat every `.py` file as a native command in contexts where a string literal or path value is expected. The direct-execution path should be explicit and test-covered.

## Runtime architecture

Recommended file layout:

- Add catalog definitions in `VirtualExecutableCatalog.cs`.
- Add command dispatch cases in `MultishellVirtualExecutableHandler.Core.cs`.
- Put Python runtime entry-point code in `MultishellVirtualExecutableHandler.Python.cs`.
- Put Perl runtime entry-point code in `MultishellVirtualExecutableHandler.Perl.cs`.
- If runtime code grows beyond handler scale, move it into a new package such as `carbide-polyglot-runtime` and keep the multishell handler as a thin adapter.

Recommended internal layers:

- `LanguageCommandLineParser` for Python and Perl option parsing.
- `LanguageRuntimeContext` holding VFS, cwd, environment, streams, executable path, argv, and dispatcher access.
- `PythonRuntime` and `PerlRuntime` as separate engines.
- Shared VFS file abstractions for text/binary stream opening.
- Shared module registry pattern for Python stdlib modules and Perl modules.
- Shared diagnostic formatter that reports unsupported features clearly.

Do not let either runtime call browser APIs, host filesystem APIs, or real process APIs directly. Every side effect should flow through shell-core abstractions that already know how to stay inside the Carbide sandbox.

## Implementation strategy

The proposal recommends a capability-first implementation rather than a parser-completeness-first implementation.

Suggested sequence:

- Add catalog entries, `Language` personality, discovery tests, and cross-shell resolution tests.
- Implement command-line parsing and version/help output for both stubs.
- Implement Python `-c`, script execution, `sys`, `os`, `os.path`, `pathlib`, `json`, `re`, and VFS-backed `open`.
- Implement Perl `-e`, `-ne`, `-pe`, regex substitution, `@ARGV`, `%ENV`, and VFS-backed file reading.
- Add shebang dispatch and `/usr/bin/env` integration after direct invocation works.
- Expand modules based on real scripts checked into or exercised by the Carbide corpus.

This keeps the surface useful from the first slice while avoiding a large, invisible parser project that only becomes valuable near the end.

## Diagnostics contract

Partial interpreters can be frustrating if they silently pretend to be complete. Diagnostics should make the boundary obvious.

Examples:

```text
python: unsupported feature: generator delegation (`yield from`)
python: unsupported module: socket
python: subprocess can only launch Carbide virtual executables
perl: unsupported switch: -T
perl: unsupported module: Socket
perl: pipe open is not supported in Carbide's VFS-only Perl stub
```

Diagnostics should include:

- Runtime name.
- Unsupported feature or option.
- Source location when available.
- Exit code matching conventional failure where practical.
- A short phrase that reminds the user this is a Carbide subset when the failure is caused by sandboxing.

## Version identity

Use Carbide-specific version strings rather than impersonating installed runtimes:

```text
Python 3.x-compatible Carbide subset
perl 5-compatible Carbide subset
```

The exact `3.x` and `5-compatible` labels should reflect implemented syntax. If the Python parser accepts modern Python 3 features such as f-strings and assignment expressions, the help text can say so. If it does not, the version output should not imply compatibility it lacks.

For scripts that inspect `sys.version` or Perl's `$]`, return values that are internally consistent but include `Carbide` in implementation metadata. This avoids a brittle lie while still allowing feature probes to proceed.

## Browser-side loading

The current single-public-endpoint plan allows browser-side dependency loading for the full executable catalog. Python and Perl runtimes will likely add more source files than ordinary utility stubs, so the browser manifest must be kept explicit.

Any new runtime files must be added to `MULTISHELL_SHARED_SOURCES` or whatever generated source manifest replaces it. If the runtime grows into a package, `carbide-pwsh` needs a project reference and the browser build needs equivalent source inclusion.

The loading contract should remain:

- One public endpoint: `carbide-pwsh`.
- Python and Perl stubs available from pwsh, cmd, bash, and nested shells.
- Runtime code loaded lazily if feasible, but behavior must not depend on which shell flavor started first.
- Stub catalog entries present before lazy runtime load, so `Get-Command python`, `where python`, and shell PATH search are stable.

## Security and sandboxing

The runtimes must preserve Carbide's sandbox expectations:

- VFS only for file access.
- Carbide environment only for environment access.
- No browser network APIs by default.
- No host process execution.
- No registry, COM, WMI, native library, dynamic loader, or OS handle access.
- No hidden persistence outside VFS.
- Resource limits for recursion depth, instruction count, output volume, regex backtracking, and file size should be considered before accepting untrusted scripts.

Python and Perl both make it easy for users to express expensive computation. The runtime should have a cancellation path connected to shell execution cancellation, and long-running regex or loops should not freeze the browser irrecoverably.

## Encoding and newline policy

Encoding is a lurking compatibility trap. Many small scripts assume UTF-8 today, but older Perl and Windows-facing scripts may assume locale encodings.

Recommended initial policy:

- Treat source files as UTF-8 with BOM tolerance.
- Treat text stdin/stdout/stderr as UTF-8 strings.
- Preserve VFS bytes for binary mode where binary mode is supported.
- Normalize command-line arguments as Unicode strings.
- Preserve line endings when reading in binary mode.
- Use `\n` for text output unless the language construct or switch requests otherwise.

Document this behavior in `--help`. If future compatibility requires Windows code pages, add them explicitly rather than inheriting host locale behavior.

## Test plan

Catalog and resolution tests:

- `pwsh`: `Get-Command python`, `Get-Command python.exe`, `Get-Command python3`, `Get-Command perl`, and `Get-Command perl.exe`.
- `cmd`: `where python`, `where python.exe`, `where perl`, and direct execution.
- `bash`: `command -v python`, `command -v python3`, `command -v perl`, and direct execution.
- Nested shell calls from `pwsh` to `cmd` and `bash` still find the same stubs.

Python behavior tests:

- `python -V` and `python --version`.
- `python -c "print('hello')"` writes `hello`.
- `python -c "import sys; print(sys.argv[1])" value` writes `value`.
- `python script.py arg1 arg2` sets `sys.argv`.
- `python - < script.py` reads program text from stdin.
- `json`, `re`, `os.path`, `pathlib`, and `glob` operate against the VFS.
- `open` cannot escape the VFS.
- `subprocess.run(["cmd", "/c", "echo hi"])` dispatches through Carbide if supported, or fails with an explicit subset diagnostic.
- `import socket` fails with an explicit unsupported-module diagnostic.

Perl behavior tests:

- `perl -v` identifies the Carbide subset.
- `perl -e "print qq(hello\n)"` writes `hello`.
- `perl -ne "print if /beta/" file.txt` filters lines.
- `perl -pe "s/foo/bar/g" file.txt` transforms lines.
- `perl -ane "print $F[0], qq(\n)" file.txt` autosplits fields.
- `perl script.pl arg1 arg2` sets `@ARGV`.
- `%ENV` reads Carbide environment variables.
- VFS-backed `open` works for read/write/append and rejects pipe open.
- `use strict; use warnings;` works as compatibility pragmas.
- `use Socket;` fails with an explicit unsupported-module diagnostic.

Integration tests:

- `#!/usr/bin/env python3` script executes from bash direct invocation.
- `#!/usr/bin/perl` script executes from bash direct invocation.
- A PowerShell script invokes Python and consumes stdout.
- A cmd batch file invokes Perl and consumes errorlevel.
- `/usr/bin/env python3 -c "print(1)"` resolves `python3` from the virtual catalog.

## Lurking issues

Language scope can balloon. Python and Perl both have deep semantics and decades of compatibility expectations. The implementation must stay anchored to real scripts and explicit feature gates.

Perl regex compatibility is hard. .NET regular expressions differ from Perl in edge cases, flags, backreferences, Unicode categories, and evaluation behavior. We can cover common substitutions and matches, but the proposal should not promise exact Perl regex semantics.

Python import behavior is deceptively large. Even small modules expect package metadata, import caching, `__file__`, `__name__`, `__package__`, and relative imports. A simple module registry can work, but arbitrary package layout support needs careful design.

`subprocess` is dangerous. Many Python scripts shell out. In Carbide, shelling out must mean invoking another virtual executable through the dispatcher. It must not become a path to host execution, and it must not imply support for background OS process behavior.

Shebang parsing crosses shell boundaries. Bash, cmd, and PowerShell have different direct-execution rules. A central dispatcher implementation is preferable to three separate shell-specific shebang hacks.

Exit-code fidelity matters. Build scripts often branch on exact failure. Unsupported-feature exits should be consistent, and `sys.exit`, Perl `exit`, uncaught exceptions, and syntax failures should map predictably.

Line buffering and streaming behavior can affect pipelines. `perl -pe` and Python filters should stream enough to work in pipelines, but Carbide's current execution model may be more batch-oriented. This needs validation against shell pipeline implementation details.

Error messages should not overfit real interpreters. Exact CPython or Perl diagnostic text is not required, and mimicking it too closely may create false expectations. Carbide-specific diagnostics are preferable.

Browser payload size can grow. Even bounded language runtimes can become source-heavy. Lazy loading is attractive, but command discovery must still work before runtime code is loaded.

Licensing and provenance must stay clean. A clean-room implementation avoids bundling interpreter code, but any borrowed grammar tables, tests, or runtime logic need license review.

## Recommendation

Proceed with `python.exe` and `perl.exe` as language-host virtual executables, not ordinary utility stubs. Add a `Language` virtual executable personality, register Python and Perl in POSIX/Git-style roots, keep them discoverable from every shell flavor, and implement bounded runtimes optimized for glue scripts.

For maximum compatibility per unit of implementation, prioritize:

- Python `-c`, script execution, VFS-backed file I/O, `sys`, `os.path`, `pathlib`, `json`, `re`, and `argparse`.
- Perl `-e`, `-ne`, `-pe`, autosplit, regex substitution, `@ARGV`, `%ENV`, VFS filehandles, `strict`, `warnings`, and `Getopt::Long`.
- Shebang and `/usr/bin/env` integration after direct invocation is reliable.

The key success criterion is not passing a broad Python or Perl conformance suite. It is making common polyglot scripts run inside Carbide with clear, sandboxed behavior and honest diagnostics when they cross the subset boundary.
