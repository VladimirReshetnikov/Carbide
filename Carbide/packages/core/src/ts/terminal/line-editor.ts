// T2 — JS-side line editor. Owns the per-keystroke echo loop in line mode, accumulates a
// buffer, and commits full lines to the C# reader via DeliverStdIn. When the C# side flips
// into key mode (via globalThis.Carbide.Terminal.setKeyMode(true)), every onData chunk is
// forwarded raw and no local echo runs.

import type { XtermTerminalLike } from "../types.js";

/** Signal path the line editor takes on receiving `\x03` (Ctrl+C). */
type CtrlCTarget = (projectId: string, signalName: string) => void;

/** Line-commit path: JS → C# DeliverStdIn(projectId, isKeyMode, data). */
type DeliverStdIn = (projectId: string, isKeyMode: boolean, data: string) => void;

export interface LineEditorOptions {
    /** The xterm instance the editor echoes back to. */
    terminal: XtermTerminalLike;
    /** Project identifier for routing `DeliverStdIn` / `DeliverSignal` calls. */
    projectId: string;
    /** The C#-side `DeliverStdIn` JSExport bound to the current runtime. */
    deliverStdIn: DeliverStdIn;
    /** The C#-side `DeliverSignal` JSExport bound to the current runtime. */
    deliverSignal: CtrlCTarget;
}

/**
 * Controller returned to the session so the key-mode flag and TreatControlCAsInput flag
 * can be flipped without the editor having to subscribe to two extra channels.
 */
export interface LineEditorController {
    /** Dispose the `onData` subscription. Idempotent. */
    dispose(): void;
    /** Toggle key mode (true: raw pass-through; false: line editor runs). */
    setKeyMode(enabled: boolean): void;
    /** Toggle Ctrl+C policy. When true, `\x03` is a byte; when false, it's a signal. */
    setTreatControlCAsInput(value: boolean): void;
}

/** Attach the line editor to an xterm instance and return a controller. */
export function attachLineEditor(options: LineEditorOptions): LineEditorController {
    const { terminal, projectId, deliverStdIn, deliverSignal } = options;

    let keyMode = false;
    let treatCtrlCAsInput = false;
    /** Buffered bytes for the current line in line mode. Cleared on commit. */
    let buffer = "";

    const subscription = terminal.onData?.((data: string) => {
        if (keyMode) {
            // Raw pass-through for key mode — the C# side's KeyParser decodes.
            deliverStdIn(projectId, true, data);
            return;
        }

        // Line mode: run the character-by-character state machine.
        for (let i = 0; i < data.length; ) {
            const ch = data[i];
            const code = data.charCodeAt(i);

            if (ch === "\x03") {
                // Ctrl+C. Route to signal or deliver as byte depending on the current policy.
                if (treatCtrlCAsInput) {
                    buffer += ch;
                    // No echo for Ctrl+C bytes; match cmd/bash defaults.
                } else {
                    deliverSignal(projectId, "SIGINT");
                }
                i += 1;
                continue;
            }

            if (ch === "\r" || ch === "\n") {
                // Commit the buffered line. Consume `\r\n` as a single line terminator so
                // a CRLF pasted block doesn't generate an empty trailing line per chunk.
                if (ch === "\r" && i + 1 < data.length && data[i + 1] === "\n") {
                    i += 2;
                } else {
                    i += 1;
                }
                terminal.write("\r\n");
                deliverStdIn(projectId, false, buffer);
                buffer = "";
                continue;
            }

            if (ch === "\x7F" || ch === "\b") {
                // Backspace: drop one char from the buffer if any; visually erase last column.
                if (buffer.length > 0) {
                    buffer = buffer.slice(0, -1);
                    terminal.write("\b \b");
                }
                i += 1;
                continue;
            }

            if (ch === "\x1b") {
                // Escape sequence — handle a minimal vocabulary (left/right cursor), swallow
                // the rest. Full line-editing with history is a T2 follow-up.
                const rest = data.slice(i);
                if (rest.startsWith("\x1b[C") || rest.startsWith("\x1b[D")) {
                    // Echo through so xterm moves the visible cursor; no buffer change in
                    // this minimal editor (we don't track an insert point yet, so the C#
                    // side only sees the post-Enter committed line).
                    terminal.write(rest.slice(0, 3));
                    i += 3;
                    continue;
                }
                // Unknown escape — swallow up to the next printable or chunk end.
                // Conservative: skip the ESC byte and let the next iteration re-sync.
                i += 1;
                continue;
            }

            if (code < 0x20) {
                // Other C0 controls: swallow silently. Tab (`\t`) is appended but not
                // echoed specially — terminals typically render it as 8 spaces by default.
                if (ch === "\t") {
                    buffer += ch;
                    terminal.write(ch);
                }
                i += 1;
                continue;
            }

            // Printable: append + echo.
            buffer += ch;
            terminal.write(ch);
            i += 1;
        }
    });

    let disposed = false;
    return {
        dispose() {
            if (disposed) return;
            disposed = true;
            subscription?.dispose();
        },
        setKeyMode(enabled: boolean) {
            keyMode = enabled;
            // Symmetric flush on either mode transition: any bytes that accumulated in the
            // wrong mode's buffer are forwarded to the new mode's consumer. Without the
            // entering-key-mode branch, keystrokes delivered before the C# state machine
            // reaches `ReadKeyAsync` (race with harnesses that deliver keys eagerly) get
            // stranded in the line-mode buffer waiting for an Enter that never comes.
            if (buffer.length > 0) {
                deliverStdIn(projectId, true, buffer);
                buffer = "";
            }
        },
        setTreatControlCAsInput(value: boolean) {
            treatCtrlCAsInput = value;
        },
    };
}
