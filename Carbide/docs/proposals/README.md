# Carbide — proposals

- Created (UTC): 2026-04-19T01:36:09Z
- Updated (UTC): 2026-04-21T23:30:00Z
- Repository HEAD: fd524f9d7350cc98cac94a770f2715a7d6bce67f

This directory holds forward-looking design proposals that build on Carbide's current planning and research corpus.

## Proposals

- [JS↔C# interop bridge proposal](carbide-js-interop-bridge-proposal__2026-04-18__22-00-00-000000__a2d2955163d1.md) — proposal for a richer JS-facing bridge over Carbide-compiled C# objects.
- [`Carbide.UI` / `@carbide-ui/*` — Avalonia GUI integration proposal](carbide-ui-avalonia-integration-proposal__2026-04-18__22-04-08-231875__2bc4122b7f3f.md) — proposal for a Carbide-adjacent Avalonia browser GUI stack.
- [PowerShell-subset shell for Carbide + xterm.js](carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md) — clean-room PowerShell-flavored shell (parser + tree-walking interpreter + VFS + cmdlet catalog) hosted as a Carbide-compiled app, supplanting the `lib/pwsh` fork path explored earlier.
- [Multi-shell (cmd + bash alongside pwsh) with cross-shell invocation](carbide-multi-shell-proposal__2026-04-21__23-30-00-000000__d9a71f3c5b68.md) — two more subset shells (`carbide-cmd` for `.cmd`/`.bat`, `carbide-bash` for `.sh`) sharing a `carbide-shell-core` with the existing pwsh implementation; any shell can invoke the others with shared VFS, env, and exit codes.
