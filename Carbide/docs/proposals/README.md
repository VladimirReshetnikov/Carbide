# Carbide — proposals

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-22T23:13:56Z
- Repository HEAD: b2dfbc1c772a37400b616ffe645ab508a54958df

This directory holds forward-looking design proposals that build on Carbide's current planning and research corpus.

## Proposals

- [JS↔C# interop bridge proposal](carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) — proposal for a richer JS-facing bridge over Carbide-compiled C# objects.
- [`Carbide.UI` / `@carbide-ui/*` — Avalonia GUI integration proposal](carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md) — proposal for a Carbide-adjacent Avalonia browser GUI stack.
- [PowerShell-subset shell for Carbide + xterm.js](carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md) — clean-room PowerShell-flavored shell (parser + tree-walking interpreter + VFS + cmdlet catalog) hosted as a Carbide-compiled app, supplanting the `lib/pwsh` fork path explored earlier.
- [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md) — two more subset shells (`carbide-cmd` for `.cmd`/`.bat`, `carbide-bash` for `.sh`) sharing a `carbide-shell-core` with the existing pwsh implementation; any shell can invoke the others with shared VFS, env, and exit codes.
- [Virtual executable stubs for common `System32` and Git `usr/bin` tools in `carbide-multishell`](carbide-multishell-vfs-executable-stubs-proposal__2026-04-22__23-10-39-000000__6827e976e1d5.md) — catalog and runtime contract for stubbed utility executables such as `robocopy.exe`, `grep.exe`, `sed.exe`, `awk.exe`, `findstr.exe`, and `tar.exe`, including exact install roots, name-collision handling, and minimum supported feature subsets.
