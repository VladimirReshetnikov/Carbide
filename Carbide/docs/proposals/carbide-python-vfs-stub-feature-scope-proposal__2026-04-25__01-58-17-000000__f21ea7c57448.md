# Proposal: Python VFS stub feature scope

- Created (UTC): 2026-04-25T01:58:17Z
- Repository HEAD: f709468966129b173d12ef792cd5027cdb4ecf62
- Status: Draft
- Audience: Vladimir; Carbide shell maintainers; agents implementing the Python virtual executable runtime
- Scope: Define the feature scope for the first Python implementation behind Carbide's `python`, `python3`, and `python.exe` virtual executable stubs.
- Related code:
  - `src/Carbide/packages/carbide-multishell/src/VirtualExecutableCatalog.cs`
  - `src/Carbide/packages/carbide-multishell/src/MultishellVirtualExecutableHandler.Core.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/VirtualExecutable.cs`
  - `src/Carbide/packages/carbide-shell-core/src/Dispatch/ShellDispatcher.cs`
  - `src/Carbide/packages/carbide-multishell-tests/VirtualExecutableTests.cs`
  - `src/Carbide/packages/carbide-shell-core/browser-shell-demo.mjs`
  - `src/Carbide/packages/carbide-pwsh/src/CarbidePwsh.csproj`
- Related docs:
  - `src/Carbide/docs/proposals/carbide-polyglot-python-perl-vfs-stubs-proposal__2026-04-25__01-45-48-877104__08ffa527c45a.md`
  - `src/Carbide/docs/proposals/carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md`
  - `src/Carbide/docs/implementation/carbide-multishell-vfs-executables-implementation-plan__2026-04-23__00-04-38-000000__16b5b67bb710.md`
- External references:
  - Python command line and environment: `https://docs.python.org/3/using/cmdline.html`
  - Python interpreter tutorial: `https://docs.python.org/3/tutorial/interpreter.html`
  - Python language reference: `https://docs.python.org/3/reference/index.html`
  - Python standard library reference: `https://docs.python.org/3/library/index.html`
  - Python `sys` module: `https://docs.python.org/3/library/sys.html`
  - Python `os` module: `https://docs.python.org/3/library/os.html`
  - Python `pathlib` module: `https://docs.python.org/3/library/pathlib.html`
  - Python `json` module: `https://docs.python.org/3/library/json.html`
  - Python `re` module: `https://docs.python.org/3/library/re.html`
  - Python `argparse` module: `https://docs.python.org/3/library/argparse.html`
  - Python `subprocess` module: `https://docs.python.org/3/library/subprocess.html`

## Summary

Carbide should implement Python first as a bounded, shell-hosted language runtime behind virtual executable stubs named `python`, `python3`, `python.exe`, and `python3.exe`. The implementation should target common polyglot scripting use cases: interactive exploration, one-liners, small scripts, VFS-backed file operations, JSON and regex transformations, argument parsing, path manipulation, and limited subprocess-style dispatch into other Carbide virtual executables.

This proposal is intentionally narrower than "implement Python." It defines a compatibility contract for the first useful Python runtime:

- Python command-line invocation compatible with the common `python [-c|-m|script|-] [args...]` forms.
- Interactive REPL mode for bare `python` in an interactive terminal and for `python -i`.
- A Python 3 language subset broad enough for ordinary glue scripts.
- VFS-only filesystem semantics.
- A small standard-library surface implemented as Carbide modules.
- Explicit unsupported-feature diagnostics instead of silent partial behavior.
- Tests that prove behavior across `pwsh`, `cmd`, `bash`, interactive terminal sessions, shebang dispatch, and `/usr/bin/env`.

The target is not CPython conformance. The target is to make Python-shaped build and automation scripts run inside Carbide's browser-safe shell environment with predictable limits.

## Positioning

The earlier Python/Perl proposal establishes that Python and Perl are **language-host virtual executables**, not ordinary filter utilities. This document specializes that decision for Python and should guide the first implementation.

Normative claims in this document:

- Python code must not access host filesystem, host process, registry, native libraries, browser APIs, network APIs, or OS handles directly.
- Python filesystem access must go through Carbide's VFS abstractions.
- Python environment access must go through Carbide's environment store.
- Python subprocess-like behavior, if supported, must dispatch through `ShellDispatcher` and must only launch Carbide-visible commands.
- Python interactive mode must use the same sandbox, module loader, VFS, environment store, cancellation model, and diagnostics as non-interactive execution.
- Unsupported Python features must fail with explicit Carbide subset diagnostics.

Descriptive or recommended claims:

- File layout, internal type names, and implementation slices are recommendations unless adopted by code.
- The exact Python syntax version label may be adjusted after the parser design is chosen.
- The exact module allow-list may grow based on real scripts and tests.

## Compatibility target

Use "Python 3-compatible Carbide subset" as the public identity. Avoid claiming exact CPython compatibility or exact Python 3.14 compatibility.

Recommended baseline:

- Accept modern Python 3 surface syntax that is common in scripts.
- Prefer Python 3.10+ semantics for pattern matching only if pattern matching is implemented.
- Prefer Python 3.8+ syntax for f-strings and assignment expressions.
- Do not rely on bytecode, `.pyc` files, CPython object layout, CPython C API, importlib internals, or native extension behavior.
- Treat Python 3.14 command-line documentation as the reference for invocation shape, but do not copy CPython implementation details that do not matter in Carbide.

Version strings should be honest:

```text
Python 3-compatible Carbide subset
```

`sys.version` should include `Carbide` and should not look like a stock CPython build. `platform.python_implementation()` can return `CarbidePython` if `platform` is implemented.

## Executable catalog and discovery

Register one command id:

```text
python
```

Recommended search names:

- `python`
- `python.exe`
- `python3`
- `python3.exe`

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

Do not register `/Windows/System32/python.exe` in the default catalog. Python is not a Windows system binary. The existing shell search roots already make `/usr/bin/python.exe` visible from `cmd` and `pwsh`.

Deferred search names:

- `py`
- `py.exe`

The Windows Python launcher has distinct version-selection behavior, so it should not be an early alias.

Recommended metadata addition:

```csharp
public enum VirtualExecutablePersonality
{
    Shell,
    Gnu,
    Windows,
    Language
}
```

The Python definition should use `Language`. If the implementation chooses not to add this enum member immediately, tests and diagnostics should still avoid calling Python a GNU tool.

## Command-line invocation contract

The command-line parser should model the important CPython interface shape without inheriting all CPython options. Python option parsing stops when an interface option consumes the program source. Arguments after that point belong to `sys.argv`.

### In-scope invocation forms

Support these in the first Python implementation:

- `python` in an interactive terminal, entering the Carbide Python REPL.
- `python script.py [args...]`
- `python3 script.py [args...]`
- `python.exe script.py [args...]`
- `python -c "code" [args...]`
- `python -m module [args...]` for an allow-listed module set.
- `python - [args...]` to read source from stdin.
- `python -i script.py [args...]` to run a script and then enter the REPL.
- `python -i -c "code" [args...]` to run inline code and then enter the REPL.
- `python -i -m module [args...]` to run an allow-listed module and then enter the REPL.
- `python -V`
- `python --version`
- `python -VV`
- `python -h`
- `python -?`
- `python --help`
- `python --help-env`
- `python --help-all`

Bare `python` behavior depends on the input mode:

- If stdin is connected to an interactive Carbide terminal, start the REPL.
- If stdin contains piped or redirected text, parse and execute stdin as a script.
- If the host cannot distinguish interactive input from redirected input, prefer an explicit shell execution-mode flag rather than guessing from an empty reader.

### `sys.argv` contract

Set `sys.argv` as follows:

| Invocation | `sys.argv[0]` | Remaining entries |
| --- | --- | --- |
| `python -c "code" a b` | `-c` | `a`, `b` |
| `python -m json.tool a.json` | module script path if known, otherwise `-m` during resolution | `a.json` |
| `python script.py a b` | script argument as provided or normalized VFS path, choose one and test it | `a`, `b` |
| `python - a b` | `-` | `a`, `b` |
| bare stdin execution | empty string or `-`, choose one and document it | arguments after interpreter options |
| bare interactive REPL | empty string | no script arguments |
| `python -i script.py a b` after script execution | same as script execution | `a`, `b`; REPL keeps the same `sys.argv` |

The implementation should preserve argument strings exactly after shell parsing. Do not reinterpret backslashes or quotes inside the Python runtime.

### `sys.path` contract

Construct `sys.path` from:

- Script directory for `python script.py`, unless `-I` or `-P` suppresses it.
- Current directory for `python -c`, `python -m`, stdin execution, and interactive REPL mode, unless `-I` or `-P` suppresses it.
- `PYTHONPATH`, unless `-E` or `-I` suppresses Python environment variables.
- A virtual standard-library root such as `/usr/lib/carbide-python`.

Do not add host paths. Do not infer paths from the user's installed Python.

### Supported generic options

Implement:

- `-h`, `-?`, `--help`: print Carbide Python help.
- `--help-env`: list Python environment variables recognized by the subset.
- `--help-all`: print full Carbide Python help, including unsupported features.
- `-V`, `--version`: print the public version string.
- `-VV`: print detailed Carbide runtime identity.

### Supported miscellaneous options

Implement or accept with defined behavior:

| Option | Behavior |
| --- | --- |
| `-B` | Accept as no-op; Carbide never writes `.pyc` files. |
| `-E` | Ignore all `PYTHON*` environment variables for runtime configuration. |
| `-I` | Isolated mode. Implies `-E`, `-P`, and no user paths. |
| `-O` | Set `__debug__` to `False`; skip `assert` execution if assertions are implemented. |
| `-OO` | Same as `-O`; docstring stripping can be a no-op at first but must be documented. |
| `-P` | Do not prepend current directory or script directory to `sys.path`. |
| `-i` | Enter the REPL after running a script, `-c` command, or `-m` module. If no program source is provided, enter the REPL directly. |
| `-q` | Suppress the interactive startup banner, while still showing prompts. |
| `-S` | Accept as no-op unless a future `site` module exists. |
| `-s` | Accept as no-op unless a future user-site model exists. |
| `-u` | Flush output eagerly where the shell stream abstraction supports it; otherwise no-op. |
| `-v` | Emit module-resolution trace for implemented imports if practical; otherwise accepted no-op with optional diagnostic in `--help-all`. |
| `-W arg` | Store warning filters if `warnings` is implemented; otherwise accept common values as no-op. |
| `-x` | Skip the first source line before parsing. Useful for DOS-style launch wrappers and cheap to support. |

### Explicitly unsupported options

Reject with a clear diagnostic:

- `-b` and `-bb` until bytes/str warnings are meaningful.
- `-d`, parser debug.
- `-R`, hash randomization control, unless a hash seed model is implemented.
- `-X ...`, implementation-specific options.
- `--check-hash-based-pycs`.
- `--help-xoptions`, unless it prints a Carbide page that says no `-X` options are supported.
- `--help-env` should not list environment variables that the subset ignores.
- `--pycache-prefix`, if accepted by the selected CPython reference version.

Unsupported-option exit code should be `2`.

## Environment variables

Recognize these in the first slice:

| Variable | Behavior |
| --- | --- |
| `PYTHONPATH` | Split into VFS path entries and append to `sys.path`, unless suppressed by `-E` or `-I`. |
| `PYTHONIOENCODING` | Accept `utf-8` and `utf-8:errors` forms. Reject unsupported encodings or ignore with explicit help text. |
| `PYTHONUTF8` | Accept as no-op because the subset should use UTF-8 text semantics by default. |
| `PYTHONUNBUFFERED` | Same behavior as `-u`. |
| `PYTHONDONTWRITEBYTECODE` | Same behavior as `-B`, effectively no-op. |
| `PYTHONWARNINGS` | Same behavior as `-W` if warnings are implemented. |
| `PYTHONSAFEPATH` | Same behavior as `-P`. |
| `PYTHONINSPECT` | Same behavior as `-i` after script, `-c`, or `-m` execution, unless suppressed by `-E` or `-I`. |
| `PYTHONSTARTUP` | VFS path to a startup script executed before the first prompt in bare interactive mode, unless suppressed by `-E` or `-I`. |

All other `PYTHON*` variables should be ignored unless the help page says otherwise. In `-E` and `-I` modes, all `PYTHON*` variables must be ignored for runtime configuration.

## Source loading and execution model

For non-interactive source, the runtime should parse an entire module before execution, matching Python's script model closely enough for syntax errors to appear before partial side effects. Interactive input uses the REPL contract below instead.

Supported source origins:

- Interactive REPL input from a Carbide terminal.
- Inline command string from `-c`.
- VFS text file from `script.py`.
- Stdin source from `-`.
- Allow-listed module from `-m`.
- Shebang-dispatched VFS script.

Deferred source origins:

- Directory execution through `__main__.py`.
- Zipfile execution.
- Package execution through `<pkg>.__main__`, except allow-listed internal modules.
- Encoded source declarations beyond UTF-8 and UTF-8 BOM.
- `.pyc` and bytecode cache loading.

Execution module identity:

- Main code should execute with `__name__ == "__main__"`.
- Main code should have `__file__` when loaded from a VFS file.
- Main code should have `__package__` as `None` or empty string unless package execution is implemented.
- Imported modules should have stable module objects in `sys.modules`.

## Interactive REPL contract

Interactive mode is in scope. It should feel like a small but real Python prompt, not a diagnostic shell bolted onto script execution.

### Entry paths

Enter interactive mode when:

- `python` is invoked with no program source and stdin is an interactive Carbide terminal.
- `python -i` is invoked with no program source.
- `python -i script.py`, `python -i -c "code"`, or `python -i -m module` completes program execution without `SystemExit`.
- `PYTHONINSPECT` is set and not suppressed by `-E` or `-I`, after script, `-c`, or `-m` execution.

Do not enter interactive mode when stdin is redirected script text and `-i` / `PYTHONINSPECT` is absent.

### Prompts and display

Use Python-style prompts:

- `sys.ps1` defaults to `>>> `.
- `sys.ps2` defaults to `... `.
- The primary prompt is used for a new input group.
- The secondary prompt is used while the parser needs more input.

The REPL should evaluate complete interactive input groups using Python's single-input semantics:

- A standalone expression displays its `repr()` through `sys.displayhook`.
- Assignment and other statements do not echo a value.
- Compound statements collect lines until complete.
- A blank line terminates an indented compound statement when the input is otherwise complete.
- Syntax errors clear the current input group and return to the primary prompt.

`sys.displayhook` should be overrideable if the object model supports assignment to module attributes. The default display hook should write to `sys.stdout` and should avoid printing `None`.

### Persistent state

The REPL owns one `__main__` module for the session:

- Variables defined at the prompt persist between input groups.
- Imports persist through `sys.modules`.
- Changes to the current directory, `sys.path`, and `os.environ` persist for later REPL input in the same Python process.
- `python -i script.py` enters the prompt with the script's `__main__` globals still available, unless the script exited through `SystemExit`.

This makes `-i` useful for inspecting script state, which is the main reason to support it.

### Startup behavior

Bare interactive mode should print a concise Carbide Python banner unless `-q` is present. The banner should identify the runtime as a Carbide subset and should not impersonate stock CPython.

If `PYTHONSTARTUP` is set and not suppressed by `-E` or `-I`, bare interactive mode should execute that VFS file in the REPL's `__main__` namespace before the first prompt. The startup path must be normalized through the VFS. Host filesystem paths must fail with a sandbox diagnostic. Startup exceptions should print a traceback and continue to the prompt unless they raise `SystemExit`.

`PYTHONSTARTUP` should not run for `python -i script.py`, `python -i -c`, or `python -i -m` unless we deliberately choose to diverge from CPython and document the divergence.

### Editing, history, and completion

The REPL should integrate with Carbide's terminal editing layer rather than implementing a separate browser text editor. The Python runtime should still provide the semantic pieces needed for a good editing experience:

- A completion provider for names in locals, globals, builtins, modules, and object attributes.
- A completion provider for importable allow-listed modules.
- A way for the terminal host to request whether the current input is complete, incomplete, or invalid.
- History entries at the complete input-group level, not one physical line at a time.
- Multiline editing support that preserves indentation and secondary prompts.

Tab completion should work when the terminal frontend supports completion requests. Up/down history should navigate complete submitted groups. If terminal-side editing capability is temporarily weaker than this contract, the REPL should still expose the semantic APIs so the UI can catch up without changing Python runtime behavior.

`readline` and `rlcompleter` can be added as compatibility modules later. The first REPL does not need to expose full GNU readline behavior.

### Cancellation and exit

Interactive cancellation should be scoped to the current input or currently executing input group:

- EOF exits the REPL with code `0`.
- `exit()` and `quit()` exit the REPL with code `0`.
- `sys.exit(n)` exits with code `n` when `n` is an integer, otherwise with Python-compatible fallback semantics.
- Keyboard interrupt during input clears the current input group and returns to the primary prompt.
- Keyboard interrupt during execution attempts to cancel execution and return to the primary prompt if runtime state remains safe.
- If cancellation leaves runtime state unsafe, terminate the Python process with a clear diagnostic rather than resuming a corrupted REPL.

The REPL must not swallow sandbox violations. Unsupported features and blocked host access should still print tracebacks or Carbide diagnostics and then return to the prompt when safe.

## Language syntax in scope

The syntax target should cover ordinary Python 3 scripts, not every new feature. The parser can reject unsupported constructs before evaluation.

### Lexical features

In scope:

- UTF-8 source with optional BOM.
- Python comments.
- Logical and physical line handling.
- Indentation and dedentation.
- Explicit line joining with backslash.
- Implicit line joining inside parentheses, brackets, and braces.
- Identifiers using the practical ASCII subset first, with Unicode identifiers deferred unless parser support is already cheap.
- Integer, float, string, bytes, boolean, and `None` literals.
- Single-quoted, double-quoted, triple-quoted, raw strings, byte strings, f-strings, and common escape sequences.

Deferred:

- Full Unicode identifier normalization.
- Full CPython tokenizer compatibility for every invalid escape warning.
- Source encoding cookies other than UTF-8.

### Expressions

In scope:

- Name lookup.
- Attribute access.
- Subscript and slicing.
- Calls with positional arguments, keyword arguments, `*args`, and `**kwargs`.
- Unary operators: `+`, `-`, `~`, `not`.
- Binary arithmetic: `+`, `-`, `*`, `/`, `//`, `%`, `**`.
- Bitwise operators: `&`, `|`, `^`, `<<`, `>>`.
- Comparisons, including chained comparisons.
- Identity and membership operators: `is`, `is not`, `in`, `not in`.
- Boolean operators: `and`, `or`.
- Conditional expression: `a if cond else b`.
- Lambdas if parser and closure model make them straightforward.
- List, tuple, dict, and set displays.
- List, dict, and set comprehensions.
- Generator expressions if iterator semantics are implemented.
- Assignment expressions `:=` if the parser can support correct scope behavior.
- F-strings in the common expression-interpolation subset.

Deferred or rejected:

- Full f-string debug syntax and nested format-spec corner cases unless cheap.
- `yield` and generator functions unless generator expressions require the machinery.
- `yield from`.
- `await`, `async for`, `async with`, and coroutine objects.
- Matrix multiplication `@`, unless trivial to wire into numeric dispatch.

### Simple statements

In scope:

- Expression statements.
- Assignment.
- Annotated assignment, with annotations stored in `__annotations__` for simple module/class targets if classes are implemented.
- Augmented assignment.
- `pass`.
- `del` for names, attributes, and indexes where the object model supports it.
- `return`.
- `raise`.
- `break`.
- `continue`.
- `import`.
- `from ... import ...`.
- `global`.
- `nonlocal` if closures are implemented.
- `assert`, with `-O` disabling assertion execution.

Deferred or rejected:

- `future` statements other than harmless recognition of common imports.
- Type alias statement from newer Python versions unless parser support is explicit.

### Compound statements

In scope:

- `if` / `elif` / `else`.
- `while` / `else`.
- `for` / `else`.
- `try` / `except` / `else` / `finally`.
- `with` for objects implementing `__enter__` and `__exit__`.
- Function definitions with default arguments, keyword-only arguments, `*args`, and `**kwargs`.
- Nested functions and closures if `nonlocal` is in scope.
- Class definitions if required by implemented modules and ordinary scripts.
- Decorators for functions and classes if classes are implemented.

Deferred or rejected:

- `async def`.
- `async with`.
- `async for`.
- Structural pattern matching (`match` / `case`) unless a real use case appears early.
- Generic type parameter lists from newer Python versions.

### Type annotations

Annotations should be syntactically accepted in common positions:

- Function parameters.
- Function return values.
- Variable annotations.
- Class body annotations if classes are implemented.

Runtime typing semantics can be minimal:

- Store simple module-level annotations in `__annotations__`.
- Do not require a full `typing` module in the first slice.
- Allow `from __future__ import annotations` as a recognized no-op if annotations are not evaluated.

This keeps modern scripts parseable without committing to the full typing ecosystem.

## Runtime object model in scope

Implement enough Python object behavior for scripts and modules to compose naturally.

### Core scalar types

In scope:

- `NoneType`
- `bool`
- `int`
- `float`
- `str`
- `bytes`
- `bytearray` as a follow-up if binary file handling needs it.

`int` should behave as arbitrary precision if practical. If the first implementation uses a fixed-width integer internally, overflow must be documented and tested because Python programmers assume unbounded integers.

### Container types

In scope:

- `list`
- `tuple`
- `dict`
- `set`
- `frozenset` if cheap.
- `range`
- `slice`

Required behavior:

- Iteration protocol.
- Length.
- Truthiness.
- Equality.
- Indexing and slicing where appropriate.
- Mutation for mutable containers.
- Stable insertion order for dictionaries.

### Callable and module types

In scope:

- User-defined functions.
- Bound methods if classes are implemented.
- Builtin functions backed by C# delegates.
- Module objects.
- Exception classes and instances.

Deferred:

- Full descriptor protocol.
- Metaclasses.
- Multiple inheritance method-resolution corner cases.
- Weak references.
- Full reflection through `inspect`.

## Builtins in scope

Prioritize builtins used by scripts, stdlib modules, and generated glue code.

### P0 builtins

Implement these first:

- `abs`
- `all`
- `any`
- `bool`
- `callable`
- `chr`
- `dict`
- `dir`
- `enumerate`
- `Exception`
- `exit`
- `filter`
- `float`
- `format`
- `getattr`
- `hasattr`
- `help` as a Carbide subset message
- `int`
- `isinstance`
- `issubclass`
- `iter`
- `len`
- `list`
- `map`
- `max`
- `min`
- `next`
- `object`
- `open`
- `ord`
- `print`
- `quit`
- `range`
- `repr`
- `reversed`
- `round`
- `set`
- `setattr`
- `slice`
- `sorted`
- `str`
- `sum`
- `tuple`
- `type`
- `zip`

### P1 builtins

Add after the first scripts run:

- `bin`
- `bytearray`
- `bytes`
- `classmethod`
- `compile`, only if it compiles to the internal AST form.
- `complex`
- `delattr`
- `divmod`
- `eval`, only if sandboxed to the same runtime and no host escape exists.
- `exec`, same constraints as `eval`.
- `frozenset`
- `globals`
- `hash`
- `hex`
- `id`, with Carbide-local identity values.
- `input`
- `locals`
- `oct`
- `pow`
- `property`
- `staticmethod`
- `super`, if classes and inheritance are implemented.
- `vars`

### Explicitly unsupported builtins

Reject or omit:

- `__import__` as a public escape hatch unless it forwards to the controlled module loader.
- `breakpoint`, unless it prints an unsupported debugger diagnostic.
- `memoryview`, unless binary buffer semantics are implemented.

`open` must be VFS-backed. It must not open host paths.

## Exception model

Implement common built-in exception classes:

- `BaseException`
- `SystemExit`
- `KeyboardInterrupt` as a cancellation mapping if applicable.
- `Exception`
- `ArithmeticError`
- `AssertionError`
- `AttributeError`
- `EOFError`
- `ImportError`
- `ModuleNotFoundError`
- `IndexError`
- `KeyError`
- `KeyboardInterrupt`
- `LookupError`
- `NameError`
- `NotImplementedError`
- `OSError`
- `FileNotFoundError`
- `FileExistsError`
- `IsADirectoryError`
- `NotADirectoryError`
- `PermissionError`
- `RuntimeError`
- `StopIteration`
- `SyntaxError`
- `IndentationError`
- `TabError`
- `TypeError`
- `ValueError`
- `ZeroDivisionError`

Exception tracebacks should include:

- Source path or `<string>` / `<stdin>` label.
- 1-based line number.
- Function name when available.
- A Python-like stack shape, without promising exact CPython formatting.

`sys.exit(value)` and `raise SystemExit(value)` must map to the virtual executable exit code.

## Import system

The import system should be simple, deterministic, and VFS-bound.

In scope:

- Builtin Carbide modules from an allow-list.
- Python source modules from VFS paths on `sys.path`.
- Packages with `__init__.py` in VFS, if straightforward.
- `from module import name`.
- Relative imports only after package metadata is implemented.
- `sys.modules` caching.
- Module attributes: `__name__`, `__file__`, `__package__`, `__loader__` as a lightweight object or string, and `__spec__` as a minimal object if needed by scripts.

Out of scope:

- Native extension modules.
- Namespace packages in the first slice.
- Zip imports.
- `.pyc` caches.
- Editable installs.
- `site-packages`.
- Import hooks and arbitrary `sys.meta_path` customization.
- `pkg_resources` and package metadata.

Import failure should distinguish:

- Module not found.
- Module exists but imports an unsupported module.
- Module source uses unsupported syntax.
- Module initialization raised an exception.

## Standard library scope

The standard library should be implemented as a curated set of Carbide modules. Each module should be small and honest. If only a subset is implemented, expose `NotImplementedError` or omit members rather than pretending the module is complete.

### P0 modules

#### `sys`

In scope:

- `argv`
- `executable`
- `version`
- `version_info`
- `implementation`
- `platform`
- `path`
- `modules`
- `stdin`
- `stdout`
- `stderr`
- `ps1`
- `ps2`
- `displayhook`
- `exit`
- `getdefaultencoding`
- `getfilesystemencoding`
- `getrecursionlimit`
- `setrecursionlimit` with bounded limits.

Out of scope:

- Audit hooks.
- Tracing/profiling hooks.
- CPython memory stats.
- Thread inspection.
- ABI flags.

#### `os`

In scope:

- `name`, likely `posix` or a Carbide-specific documented value.
- `environ` backed by Carbide environment variables.
- `getcwd`
- `chdir`
- `listdir`
- `scandir` with a small `DirEntry`.
- `stat` with a bounded stat object.
- `mkdir`
- `makedirs`
- `remove`
- `unlink`
- `rmdir`
- `removedirs` if cheap.
- `rename`
- `replace`
- `walk`
- `getenv`
- `putenv` and `unsetenv` if they update Carbide env consistently.
- `sep`, `altsep`, `pathsep`, `linesep`.
- `fspath`
- `PathLike` if needed by `pathlib`.

Out of scope:

- `fork`
- `exec*`
- `spawn*`
- `system`, unless it dispatches safely through Carbide and is explicitly marked.
- `popen`
- Process ids as real OS ids.
- Signals.
- File descriptors as OS handles.
- Permissions beyond VFS metadata.

#### `os.path`

In scope:

- `abspath`
- `basename`
- `commonpath`
- `commonprefix`
- `dirname`
- `exists`
- `expanduser` against Carbide home semantics.
- `expandvars` against Carbide env.
- `getatime`, `getmtime`, `getctime`, `getsize` if VFS metadata supports them.
- `isabs`
- `isdir`
- `isfile`
- `join`
- `normpath`
- `realpath` as VFS normalization, with no host symlink promises unless VFS supports symlinks.
- `relpath`
- `split`
- `splitdrive`, returning empty drive for POSIX-style VFS unless Windows-path compatibility is added.
- `splitext`

#### `pathlib`

In scope:

- `PurePath`
- `PurePosixPath`
- `PureWindowsPath` for pure string operations.
- `Path` mapped to VFS semantics.
- Construction, joining, `name`, `suffix`, `stem`, `parent`, `parents`, `parts`.
- `exists`, `is_file`, `is_dir`.
- `iterdir`, `glob`, `rglob` with bounded recursion.
- `read_text`, `write_text`, `read_bytes`, `write_bytes`.
- `open`, `mkdir`, `unlink`, `rename`, `replace`.

Out of scope:

- Owner/group.
- chmod.
- Real symlink behavior unless VFS supports it.
- Device/inode identity.

#### `json`

In scope:

- `loads`
- `dumps`
- `load`
- `dump`
- Basic options: `indent`, `sort_keys`, `ensure_ascii`, `separators`, `default` if callable dispatch is ready.
- `JSONDecodeError`.

Out of scope:

- Exact CPython scanner diagnostics.
- Full subclassing hooks if object model cannot support them yet.

#### `re`

In scope:

- `compile`
- `search`
- `match`
- `fullmatch`
- `findall`
- `finditer`
- `split`
- `sub`
- `subn`
- `escape`
- `Pattern`
- `Match`
- Common flags: `I`, `M`, `S`, `X`, `A`.

Implementation can be backed by .NET regular expressions, but docs and tests must call out semantic differences from CPython `re`.

Out of scope:

- Exact CPython regex engine behavior.
- Locale-sensitive regex behavior.
- Every Unicode category edge case.
- Catastrophic-backtracking parity. Carbide should prefer resource protection over exact slowness.

#### `argparse`

In scope:

- `ArgumentParser`
- Positional arguments.
- Optional flags.
- Short and long options.
- `store`, `store_true`, `store_false`, `append`, and `count` actions.
- `default`, `required`, `choices`, `nargs` for common values.
- Help formatting good enough for scripts.
- `parse_args` and `parse_known_args`.
- `SystemExit` behavior on parse failure.

Out of scope:

- Every formatter class.
- Shell completion.
- Localization.
- Exotic conflict handlers.

#### `glob` and `fnmatch`

In scope:

- `glob.glob`
- `glob.iglob`
- `fnmatch.fnmatch`
- `fnmatch.fnmatchcase`
- `fnmatch.filter`
- `*`, `?`, and bracket character classes.
- Recursive `**` with a resource limit.

#### `textwrap`

In scope:

- `dedent`
- `indent`
- `wrap`
- `fill`
- `shorten`

`textwrap.dedent` is particularly useful for inline script tests and generated snippets.

### P1 modules

#### `collections`

In scope:

- `Counter`
- `defaultdict`
- `deque`
- `OrderedDict` as dict-compatible behavior.
- `namedtuple` if dynamic class creation is implemented.

#### `itertools`

In scope:

- `chain`
- `count`
- `cycle` with caution for infinite iteration.
- `repeat`
- `islice`
- `takewhile`
- `dropwhile`
- `filterfalse`
- `zip_longest`
- `product`
- `permutations`
- `combinations`
- `groupby`

#### `math`

In scope:

- Common numeric functions backed by .NET `Math`.
- Constants `pi`, `e`, `tau`, `inf`, `nan`.

Out of scope:

- Exact floating-point edge-case parity for all functions.

#### `datetime` and `time`

In scope:

- `datetime.date`
- `datetime.time`
- `datetime.datetime`
- `datetime.timedelta`
- `timezone.utc`
- `time.time`
- `time.monotonic`
- `time.sleep` as cooperative shell sleep if cancellation-safe.
- Basic formatting and parsing helpers.

Out of scope:

- Full timezone database.
- Host locale time formatting.
- High-resolution process/thread clocks.

#### `csv`

In scope:

- `reader`
- `writer`
- `DictReader`
- `DictWriter`
- Dialect options commonly used by scripts.

#### `shutil`

In scope:

- `copyfile`
- `copy`
- `copy2` as `copy` plus best-effort metadata.
- `copytree`
- `rmtree`
- `move`
- `which` backed by `ShellDispatcher` resolution.

Out of scope:

- Disk usage from host filesystem.
- Ownership/permission propagation.
- Archive formats until `zipfile` or `tarfile` exists.

#### `tempfile`

In scope:

- `gettempdir`
- `NamedTemporaryFile` backed by VFS.
- `TemporaryDirectory` backed by VFS.
- `mkstemp` and `mkdtemp` if file descriptor semantics can be represented safely.

### P2 modules

Add only after real scripts or tests justify them:

- `fileinput`
- `configparser`
- `platform`
- `base64`
- `hashlib` with browser-safe managed implementations.
- `urllib.parse`, not network I/O.
- `zipfile` for VFS zip processing if useful.
- `tarfile` if useful and payload is manageable.
- `unittest` for simple script-driven tests.
- `doctest`.
- `typing`, mostly to keep modern scripts importable.
- `dataclasses`.
- `enum`.
- `functools`.
- `operator`.
- `copy`.
- `pprint`.
- `traceback`.
- `types`.

## `subprocess` scope

`subprocess` is in scope only as a dispatcher facade. It must not spawn host processes.

P1 supported API:

- `subprocess.run`
- `subprocess.check_call`
- `subprocess.check_output`
- `subprocess.CalledProcessError`
- `subprocess.CompletedProcess`
- Constants `PIPE`, `STDOUT`, `DEVNULL` with Carbide-local semantics.

Supported behavior:

- Command is a string or list.
- Executable resolves through `ShellDispatcher`.
- stdin can be text, bytes if binary streams exist, or inherited from Python stdin.
- stdout/stderr can be captured as text.
- `check=True` raises `CalledProcessError` on nonzero exit.
- `cwd` changes only Carbide runtime cwd for the dispatched command.
- `env` overlays Carbide environment for the dispatched command.

Unsupported behavior:

- Background process lifetime.
- Real `Popen` streaming handles.
- OS signals.
- Process groups.
- File descriptor inheritance.
- Shell injection into host shell.
- Host executable paths.

If `Popen` is exposed, it should be a restricted compatibility object that completes synchronously or through Carbide's cooperative execution model. It must not imply real OS process behavior.

## File I/O and VFS behavior

`open` and module file APIs must use Carbide VFS.

Supported modes:

- `r`
- `w`
- `a`
- `x`
- `r+`, `w+`, and `a+` if VFS stream mutation supports them cleanly.
- Text mode by default.
- Binary mode if `bytes` support exists.

Unsupported modes:

- OS-level file descriptor wrapping.
- Exclusive flags beyond VFS `x` creation semantics.
- Memory mapping.
- File locking.
- Permission changes.

Text encoding policy:

- Default to UTF-8.
- Accept `encoding="utf-8"`.
- Accept `errors` values that can be implemented by .NET encoders.
- Reject unsupported encodings with `LookupError`.
- Normalize newlines according to Python text-mode expectations where practical.

Path policy:

- Accept POSIX-style VFS paths.
- Accept Windows-looking `C:\...` paths by normalizing to Carbide's VFS convention if the existing shell path normalization supports that.
- Never pass a path to host APIs.

## Shebang and direct script execution

Python should participate in direct script dispatch.

Supported shebangs:

```text
#!/usr/bin/env python
#!/usr/bin/env python3
#!/usr/bin/python
#!/usr/bin/python3
```

Supported direct execution:

- `.py` file invoked from bash when executable-script dispatch is implemented.
- `.py` file invoked from pwsh or cmd only through explicit dispatcher rules, not by accidentally treating every path string as executable.

The shebang dispatcher should:

- Parse the first line without requiring the full Python runtime to load.
- Resolve `/usr/bin/env` through the virtual executable catalog.
- Pass script path and arguments to Python exactly once.
- Avoid shell-specific duplicate implementations.

## Diagnostics

Diagnostics are part of the compatibility contract. The first implementation should be unapologetically explicit.

Examples:

```text
python: unsupported option: -X importtime
python: unsupported module: socket
python: unsupported syntax at /work/tool.py:12: async def
python: subprocess can only launch Carbide virtual executables
python: open() is restricted to the Carbide VFS: C:\Users\vresh\secret.txt
```

Syntax errors should include:

- Source path or source label.
- Line number.
- Column number if available.
- Offending line where practical.

Runtime errors should include:

- Python-like traceback.
- Exception type.
- Exception message.

Unsupported-feature diagnostics should include:

- Feature name.
- Whether the feature is unsupported because of language subset, sandboxing, or missing module.
- Exit code `1` for runtime errors and `2` for command-line usage errors.

## Security and resource limits

The Python runtime is a sandboxed compatibility layer, not a security boundary made of CPython audit hooks. Carbide must enforce the boundary by construction.

Required restrictions:

- No host filesystem access.
- No host process access.
- No native library loading.
- No network by default.
- No registry, COM, WMI, browser DOM, or browser storage access.
- No direct JavaScript interop unless a future API explicitly grants it.

Recommended resource controls:

- Maximum recursion depth.
- Maximum AST size.
- Maximum executed statement or instruction count.
- Maximum call stack depth.
- Maximum output size per command or per stream, aligned with shell behavior.
- Maximum file read size for text helpers if VFS lacks streaming.
- Regex execution timeout or step limit.
- Cancellation checks in loops, comprehensions, file iteration, and regex operations.

When a resource limit is hit, raise a Carbide-specific runtime error that is visible as a Python exception if it can be caught safely, or terminate execution with a clear diagnostic if continuing would be unsafe.

## Implementation architecture

Recommended source shape:

- `VirtualExecutableCatalog.cs`: add Python definitions and stub paths.
- `VirtualExecutable.cs`: add `Language` personality if accepted.
- `MultishellVirtualExecutableHandler.Core.cs`: dispatch `python`.
- `MultishellVirtualExecutableHandler.Python.cs`: thin executable entry point.
- `PythonCommandLine.cs`: parse interpreter options.
- `PythonRuntimeContext.cs`: VFS, env, cwd, streams, dispatcher, argv, executable path.
- `PythonLexer.cs`: tokenize source.
- `PythonParser.cs`: produce an AST.
- `PythonAst.cs`: syntax model.
- `PythonInterpreter.cs`: evaluate AST.
- `PythonInteractiveSession.cs`: own REPL state, prompts, history-group boundaries, cancellation, and `__main__` persistence.
- `PythonCompleter.cs`: provide REPL completion candidates for names, attributes, modules, and imports.
- `PythonObjects.cs`: object model.
- `PythonModules.*.cs`: built-in modules.

If this grows beyond a handler-scale feature, create a dedicated package:

```text
src/Carbide/packages/carbide-python-runtime/
```

The public shell endpoint should not change. `carbide-pwsh` remains the single public endpoint and loads Python runtime support as part of the shared virtual executable catalog.

## Implementation slices

Use implementation slices as capability gates, not calendar estimates.

### Slice 1: Executable shell

Artifacts:

- Catalog stubs.
- `Language` personality or equivalent metadata.
- Command-line parser.
- Version/help output.
- Unsupported-option diagnostics.
- Cross-shell discovery tests.

Exit criteria:

- `Get-Command python` works from pwsh.
- `where python` works from cmd.
- `command -v python` works from bash.
- `python -V` and `python --help` work.

### Slice 2: Minimal evaluator and REPL

Artifacts:

- Lexer/parser/interpreter for expressions, assignment, `if`, `while`, `for`, functions, and imports from built-in modules.
- Core object model.
- Core builtins.
- `sys` module.
- `python -c` and `python script.py`.
- Bare `python` interactive mode in terminal sessions.
- `python -i` after script and `-c` execution.
- Primary/secondary prompts, expression display, persistent `__main__`, and minimal completion provider.

Exit criteria:

- `python -c "print('hello')"` works.
- `python -c "import sys; print(sys.argv[1])" value` works.
- A script with functions, loops, lists, dictionaries, and exceptions runs.
- Bare `python` shows a Carbide Python banner and prompt, evaluates `1 + 2`, persists `x = 3`, and exits on EOF.
- `python -q -i -c "x = 41"` enters the REPL without a banner and evaluates `x + 1` as `42`.
- Incomplete compound input uses the secondary prompt and executes after the terminating blank line.

### Slice 3: VFS and practical modules

Artifacts:

- VFS-backed `open`.
- `os`, `os.path`, `pathlib`.
- `json`, `re`, `argparse`, `glob`, `fnmatch`, `textwrap`.
- Basic traceback formatting.

Exit criteria:

- Python can read/write VFS files.
- Python can transform JSON.
- Python can use regex substitutions.
- Python can parse command-line arguments.
- Python cannot escape the VFS.

### Slice 4: Script integration

Artifacts:

- `python -m json.tool`.
- `/usr/bin/env python3` dispatch.
- `.py` direct execution where shell semantics allow it.
- `PYTHONPATH`, `-E`, `-I`, and `-P` tests.
- `PYTHONSTARTUP` and `PYTHONINSPECT` tests for interactive mode.

Exit criteria:

- A shebang Python script runs from bash.
- `python -m json.tool file.json` works.
- Importing a VFS module through `PYTHONPATH` works when allowed and fails when isolated.
- Bare interactive mode executes a VFS `PYTHONSTARTUP` script when allowed and ignores it under `-E` or `-I`.

### Slice 5: Dispatcher subprocess facade

Artifacts:

- Bounded `subprocess`.
- `shutil.which`.
- Env and cwd overlays for dispatched commands.

Exit criteria:

- `subprocess.run(["grep", "needle", "file.txt"], capture_output=True, text=True)` resolves through Carbide and returns a completed process.
- Host-only paths fail clearly.
- Background process assumptions fail clearly.

## Test matrix

### Discovery tests

- Stub files exist under `/usr/bin`, `/bin`, and `/Program Files/Git/usr/bin`.
- `python`, `python.exe`, `python3`, and `python3.exe` resolve from `pwsh`.
- The same names resolve from `cmd`.
- The same names resolve from `bash`.
- `Get-Command python` reports an implemented application command.

### CLI tests

- `python -V`.
- `python -VV`.
- `python --help`.
- `python --help-env`.
- `python -c "print('x')"`.
- `python -c "import sys; print(sys.argv)" a b`.
- `python - < /work/script.py`.
- `python -q -i -c "x = 41"` enters interactive mode and suppresses the banner.
- `python -i /work/script.py arg` preserves script globals and `sys.argv` in the REPL.
- `PYTHONINSPECT=1 python /work/script.py` enters interactive mode unless `-E` or `-I` is present.
- Unsupported `-X importtime` returns exit code `2`.

### Interactive REPL tests

- Bare `python` in an interactive terminal prints a Carbide Python banner and primary prompt.
- `python -q` enters the REPL without the banner.
- A standalone expression prints `repr(result)`.
- A statement such as `x = 10` produces no echoed value.
- Later input can evaluate `x + 5` as `15`.
- Compound input uses `... ` prompts until a terminating blank line.
- Syntax error returns to the primary prompt without losing previous globals.
- Runtime exception prints a traceback and returns to the primary prompt.
- `exit()`, `quit()`, EOF, and `sys.exit(7)` produce the expected exit behavior.
- Keyboard interrupt during input clears the current input group.
- Completion includes globals, builtins, imported modules, and object attributes when the terminal frontend requests completions.
- History stores complete input groups, including multiline compound statements.
- `PYTHONSTARTUP` executes a VFS script before the first prompt in bare interactive mode.
- `PYTHONSTARTUP` is ignored when `-E` or `-I` is present.

### Syntax and runtime tests

- Arithmetic and comparisons.
- String formatting and f-strings.
- Lists, tuples, dictionaries, sets.
- Loops and comprehensions.
- Functions and closures if implemented.
- Exceptions and tracebacks.
- Imports and `sys.modules`.
- Assertions under normal mode and `-O`.

### VFS tests

- `open("file.txt").read()`.
- `Path("dir").glob("*.txt")`.
- `os.listdir`.
- `os.walk`.
- Write, append, and exclusive create.
- Attempted host absolute path fails.

### Module tests

- `json.loads` / `json.dumps`.
- `python -m json.tool`.
- `re.sub`.
- `argparse.ArgumentParser`.
- `glob.glob`.
- `textwrap.dedent`.
- `shutil.which` once subprocess facade exists.

### Integration tests

- PowerShell invokes Python and consumes stdout.
- cmd invokes Python and observes `%ERRORLEVEL%`.
- bash pipeline pipes text into Python stdin.
- Shebang script dispatches through `/usr/bin/env python3`.
- Python `subprocess.run` invokes another virtual executable.

### Negative tests

- `import socket` fails.
- `import ctypes` fails.
- `open` cannot access host paths.
- `subprocess.run` cannot launch host paths.
- `async def` fails until supported.
- Native extension import fails.
- Unsupported encoding fails with `LookupError`.

## Lurking issues

Parser scope can expand silently. Python's grammar is friendly until it is not: f-strings, annotations, pattern matching, lambdas, comprehensions, closures, and class bodies all interact with scope. The implementation should reject unsupported syntax early rather than parse it into broken runtime behavior.

Import semantics can become the largest subsystem. Even small scripts rely on `sys.path`, `__file__`, module caching, package initialization, and relative imports. The first implementation should keep imports simple and test every supported path shape.

`pathlib` can expose platform contradictions. Carbide VFS is neither Windows nor POSIX in the host sense. The proposal recommends POSIX-like VFS behavior for concrete `Path`, with `PureWindowsPath` available only for string manipulation.

Regex behavior will differ if .NET regex backs `re`. This is acceptable only if documented and bounded. The runtime should favor predictable, safe regex execution over exact CPython edge behavior.

`subprocess` is the highest-risk module. It is valuable for scripts, but it must never become host process execution. The facade should be implemented late enough that dispatcher boundaries and tests are already solid.

Interactive mode couples runtime semantics to terminal UX. The REPL needs Python-level state management, but editing, history navigation, completion display, multiline prompts, and cancellation all cross into the shell frontend. The implementation should keep a clean semantic boundary: Python decides whether input is complete and what completions exist; the terminal decides how to render and edit them.

Modern Python code may import `typing`, `dataclasses`, or `pathlib` even when the runtime behavior is simple. If real scripts fail primarily on import availability, small compatibility modules may produce better value than deeper language features.

Some scripts use Python as a JSON processor. `json`, `sys`, `open`, and `pathlib` should therefore be treated as first-class compatibility features, not optional niceties.

## Recommendation

Proceed with Python first as a bounded Carbide language runtime. The first meaningful target is:

- Cross-shell stubs for `python`, `python3`, `python.exe`, and `python3.exe`.
- Bare `python` interactive REPL mode, `python -i`, `python -c`, `python script.py`, `python -`, and `python -m json.tool`.
- Core Python expressions and statements used by glue scripts.
- VFS-backed `open`, `os`, `os.path`, and `pathlib`.
- `sys`, `json`, `re`, `argparse`, `glob`, `fnmatch`, and `textwrap`.
- Honest diagnostics for unsupported syntax, unsupported modules, unsupported options, and sandbox violations.

Do not start by chasing broad CPython conformance. Start by making small real automation scripts work in the Carbide sandbox, with a test matrix that makes every supported feature explicit.
