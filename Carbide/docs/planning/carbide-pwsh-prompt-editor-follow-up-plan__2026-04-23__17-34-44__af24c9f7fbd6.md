# Carbide-pwsh Prompt Editor Follow-up Plan

- Created (UTC): 2026-04-23T17:34:44Z
- Repository HEAD: d2e314726d4c317ed90f10f83ce200d8e6234112
- Status: Draft
- Audience: Maintainers, reviewers, follow-up implementers
- Scope: Next-stage interactive prompt work for `carbide-pwsh` after the initial lightweight editor landed
- Related code:
  - `src/Carbide/packages/carbide-pwsh/src/Host/PwshPromptEditor.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Program.cs`
  - `src/Carbide/packages/carbide-pwsh/src/Host/ShellHost.cs`
  - `src/Carbide/packages/core/src/Terminal/CarbideConsole.cs`
  - `src/Carbide/packages/core/src/ts/terminal/line-editor.ts`
- Related docs:
  - [carbide-pwsh Phase 3 plan](carbide-pwsh-phase3-detailed-plan__2026-04-21__23-00-00-000000__f8c3e2a9b471.md)
  - [PowerShell-subset shell proposal](../proposals/carbide-pwsh-subset-shell-proposal__2026-04-21__21-30-00-000000__e9c4b27a8f13.md)
  - [xterm.js interactive console plan](carbide-xterm-interactive-console-plan__2026-04-19__23-34-41-000000.md)

## Summary

`carbide-pwsh` now has a host-owned lightweight prompt editor instead of raw `ReadLineAsync()`
input. The shipped baseline covers the highest-value interactive conveniences that fit the
current architecture without pulling `carbide-pwsh` into a PSReadLine-sized subsystem:
line clearing, Ctrl+C line abandon, recent-history navigation, command-name completion,
basic cursor movement, delete/backspace, and redraw/clear-screen helpers. This document
defines the next improvements that are worth doing once we are ready to spend more scope on
the prompt as a product surface rather than a thin REPL helper.

The plan is intentionally scoped to interactive editing and prompt-adjacent UX. It does not
redefine the parser, replace the Carbide terminal stack, or attempt full upstream
PowerShell/PSReadLine parity in one step.

## Scope and non-goals

### In scope

- richer input editing behavior in the `carbide-pwsh` prompt;
- better completion semantics for command, parameter, path, and member positions;
- history behavior that matches PowerShell expectations more closely;
- prompt-specific interrupt/cancellation behavior where the current execution model can
  support it;
- validation infrastructure for browser and local-console prompt behavior.

### Out of scope

- full PSReadLine feature parity;
- terminal-wide features that belong in `@carbide/core` for every interactive app;
- remoting, job control, background runspace support, or full `Ctrl+Z` shell semantics;
- a wholesale rewrite of the browser terminal bridge;
- persistent profile loading and full prompt customization engine.

## Current landed baseline

The current prompt editor lives in `PwshPromptEditor` and is deliberately host-owned rather
than embedded in the generic JS line editor. That split is useful and should remain true:

- `@carbide/core` still owns terminal transport, key delivery, and the generic key-mode vs
  line-mode bridge.
- `carbide-pwsh` owns PowerShell-specific editing semantics, history policy, and command
  completion candidates.
- The browser path reuses `CarbideConsole.ReadKeyAsync(bool, CancellationToken)` via a
  reflective bridge so `carbide-pwsh` does not need a compile-time dependency on
  `Carbide.Terminal`.

The baseline behavior now includes:

- `Esc` clears the current line.
- `Ctrl+C` abandons the current line, emits red `^C`, and returns control to the next prompt
  loop iteration.
- `UpArrow` / `DownArrow` navigate the in-memory prompt history.
- `Tab` and `Shift+Tab` cycle command-name completion candidates gathered from builtin
  cmdlets, aliases, functions, app names, and recognized builtin placeholders.
- `LeftArrow` / `RightArrow`, `Home` / `End`, `Ctrl+A` / `Ctrl+E`, `Backspace`, `Delete`,
  and `Ctrl+L` work inside the current line.
- The REPL loop in `Program.cs` understands prompt interruption and pending multi-line input.

That baseline is intentionally narrower than upstream `pwsh`. The remaining gaps below are
the ones that become worth addressing only after the lightweight editor has proven itself.

## Follow-up workstreams

## 1. Multi-line-aware editing and history ownership

### Problem

The current editor is line-oriented. The REPL loop can collect multi-line submissions, but
history and editing are still shaped like independent prompt lines rather than one logical
command entry. This creates several gaps:

- an incomplete first line can be remembered before the full command is complete;
- recalled history is fundamentally single-line even when the command that created it was
  logically multi-line;
- editing behavior across continuation prompts is simple rather than PowerShell-like.

### Proposed direction

Move history ownership from the low-level prompt editor toward the REPL/session layer and
let the prompt editor operate on a command buffer abstraction instead of a single string.

The minimal viable design should introduce:

- one logical `PromptBuffer` or equivalent value object that can represent one or more
  physical lines;
- explicit commit/cancel hooks so the host decides when history entries are recorded;
- redraw helpers that understand prompt width plus continuation prompts, not just one
  `prompt + buffer` string;
- history entries stored as complete logical commands, with a conservative fallback for
  rendering them in the current terminal.

### Expected size

- roughly 250-500 LOC across `PwshPromptEditor.cs`, `Program.cs`, and 1-2 new helper files;
- 6-12 new unit/integration tests.

### Key risk

Cursor positioning over multiple physical lines is where the current host-local approach
stops being trivial. We should not attempt this without first deciding whether the terminal
surface we rely on can give us dependable row/column behavior in both local-console and
browser-hosted xterm sessions.

## 2. Token-aware and argument-aware completion

### Problem

Current completion only handles the first token of a pipeline segment and only as a command
name prefix match. That is enough for a first prompt baseline, but it leaves large gaps
compared to `pwsh`:

- parameter-name completion (`-Err<Tab>`);
- path completion after cmdlets and native-ish commands;
- member completion after `.` / `::`;
- variable completion after `$`;
- completion filtered by command position vs expression position;
- quoting/escaping rules for paths with spaces.

### Proposed direction

Split completion into a small prompt-facing orchestrator plus a semantic completion service.
The service should be driven by parser/lexer facts instead of ad hoc string slicing.

The first staged target should be:

1. command-position completion using parser-aware token boundaries;
2. parameter completion for known cmdlets/functions;
3. VFS path completion for path-valued positions;
4. expression/member completion only after the earlier steps are stable.

The right long-term seam is probably a `CompletionService` under `src/Cmdlets/Discovery/`
or a new `src/Host/Completion/` folder, not more logic inside `PwshPromptEditor`.

### Expected size

- roughly 400-900 LOC across 4-8 files;
- 12-25 tests spanning parser-aware completion, VFS path quoting, and host integration.

### Key risk

Completion becomes misleading if it ignores binding semantics. We should keep the early
versions intentionally conservative rather than pretending to know more than the binder and
parser can currently prove.

## 3. Better history semantics and search

### Problem

The current in-memory ring is intentionally small and session-local. It does not support:

- deduplication or suppression policies;
- prefix search;
- reverse incremental search;
- history persistence across sessions;
- history filtering by the current typed prefix.

### Proposed direction

Stage history upgrades in this order:

1. better in-memory semantics:
   - optional duplicate suppression;
   - prefix-aware `UpArrow` / `DownArrow` behavior;
   - history recording only at logical-command commit;
2. session persistence:
   - a PowerShell-specific history file under a Carbide-owned state location;
   - opt-in or clearly documented behavior in browser-hosted sessions;
3. search UX:
   - reverse search (`Ctrl+R`) only after we have a durable line-state model.

### Expected size

- stage 1: ~120-250 LOC across 2-3 files;
- stage 2: ~100-250 LOC plus persistence-path policy;
- stage 3: another ~150-350 LOC because it implies prompt-state UI, not just storage.

### Key risk

Persisted history in browser-hosted Carbide sessions raises policy questions: should it live
in VFS, IndexedDB-backed host state, or only in the page lifetime? That decision belongs in
the host/session model, not only inside `carbide-pwsh`.

## 4. Prompt rendering, selection, and edit commands

### Problem

The current editor handles plain insertion/deletion/cursor movement only. Once we go beyond
that, the missing features start to cluster:

- word-wise navigation and deletion;
- transpose/kill/yank-style edit commands;
- overwrite vs insert semantics if we ever want them;
- selection-aware clipboard integration in the browser path;
- prompt redraw rules for wide characters, ANSI sequences in prompt text, and wrapped lines.

### Proposed direction

Treat this as a separate polish tranche after multi-line support, not as a grab-bag of
shortcuts. The important architectural move is to have one cursor/selection model with
terminal-facing rendering helpers, rather than continuing to grow imperative cursor math in
one method.

The likely shape is:

- `PromptBuffer` / `PromptCursor` primitives;
- one renderer responsible for VT redraw sequences;
- edit commands mapped from key chords onto those primitives.

### Expected size

- roughly 300-700 LOC across 3-6 files, depending on how much buffer abstraction already
  exists from workstream 1.

### Key risk

This is where duplicated editing logic between `carbide-pwsh` and the generic JS line editor
starts to hurt. Before landing a large batch here, we should consciously decide whether a
shared editor core is actually warranted, or whether `carbide-pwsh` should remain the only
consumer of these richer semantics.

## 5. Interrupt and cancellation semantics beyond line abandon

### Problem

Current `Ctrl+C` behavior is prompt-local: it abandons the line being edited. That is the
right first step, but it is not the whole `pwsh` story. Users also expect a way to break
running commands or scripts when feasible.

### Proposed direction

Separate two cases:

- **editing-time interrupt**: already shipped; keep it local to `PwshPromptEditor`;
- **execution-time interrupt**: plumb a cancellation token or cooperative interrupt flag
  through the interpreter/cmdlet/app dispatch path where practical.

The implementation should stay honest: if cancellation only reaches some cmdlets and not
arbitrary user script loops, say so. Do not imitate upstream shell semantics more broadly
than the interpreter can really support.

### Expected size

- small host plumbing only: ~100-220 LOC across `Program.cs`, `ShellHost.cs`, and
  interpreter entry points;
- interpreter/cmdlet propagation: another ~200-600 LOC across whichever execution surfaces
  opt in.

### Key risk

Partial cancellation is better than fake cancellation, but only if the shell surfaces that
boundary clearly. A misleading `Ctrl+C` that sometimes looks successful while work keeps
running would be worse than today's explicit limitation.

## 6. Validation and browser-specific regression coverage

### Problem

Most prompt behavior is currently protected by unit tests over a fake console. That is good
for editing-state logic, but thin for browser-specific key delivery and redraw behavior.

### Proposed direction

Add a small prompt-focused browser smoke layer on top of the existing `scripts/smoke.mjs`
path or a sibling Playwright harness. The high-value cases are:

- prompt appears and accepts input after browser boot;
- `Esc` clears input;
- `Ctrl+C` emits red `^C` and returns to a fresh prompt;
- `UpArrow` history recall works in the browser path;
- `Tab` completion works against the browser-delivered key stream.

### Expected size

- roughly 80-200 LOC across 1-3 browser test files plus small script tweaks.

### Key risk

These tests will be noisy if they overfit xterm buffer formatting. Favor assertions on
stable visible substrings and prompt state over exact whole-screen snapshots.

## Recommended staging order

If we continue this work, the highest-value order is:

1. browser regression coverage for the currently shipped baseline;
2. multi-line-aware history ownership;
3. token-aware parameter/path completion;
4. better history semantics and search;
5. richer edit-command surface;
6. execution-time interrupt/cancellation follow-up.

This order intentionally prioritizes correctness and architectural seams over adding more key
chords first.

## Notes for maintainers

- Keep the split between generic terminal transport (`@carbide/core`) and PowerShell-specific
  editing policy (`carbide-pwsh`) unless a shared need actually materializes.
- Do not turn `PwshPromptEditor` into a second parser. Completion, history policy, and
  semantic command discovery should move into dedicated collaborators once they stop being
  trivial.
- Prefer conservative parity claims. "`pwsh`-like conveniences" is accurate today; "PSReadLine
  parity" is not.
- If we add persistence or cancellation, document the browser/local-console differences in the
  package README and in a Carbide project-level doc, not only in tests.
