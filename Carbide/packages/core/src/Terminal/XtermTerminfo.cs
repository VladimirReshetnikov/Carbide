// T2 — minimal terminfo-shaped shim for the browser target. Mirrors the parts of upstream
// `TerminalFormatStrings` that <see cref="KeyParser"/> consults. xterm.js emulates
// xterm-256color, so we only populate the xterm key-format table; rxvt/SCO alternate
// codepaths are dead in this shim (KeyParser still has the fall-through maps for safety).

using System.Collections.Generic;

namespace Carbide.Terminal;

/// <summary>
/// Surface <see cref="KeyParser"/> needs: <see cref="KeyFormatToConsoleKey"/> (escape
/// sequence → <see cref="ConsoleKeyInfo"/> lookup) and <see cref="IsRxvtTerm"/> (false for
/// xterm.js).
/// </summary>
internal sealed class XtermTerminfo
{
    public static XtermTerminfo Shared { get; } = new();

    public bool IsRxvtTerm => false;

    /// <summary>
    /// Pre-seeded with the xterm-256color key bindings that Carbide actually sees from
    /// xterm.js. KeyParser's generic fall-through maps cover anything not in the table; the
    /// explicit entries here let the fast-path dictionary hit win on common keys.
    /// </summary>
    public Dictionary<string, ConsoleKeyInfo> KeyFormatToConsoleKey { get; } = Build();

    private static Dictionary<string, ConsoleKeyInfo> Build()
    {
        // Entries modeled on xterm's `infocmp xterm-256color` output, trimmed to what
        // xterm.js actually emits.
        var db = new Dictionary<string, ConsoleKeyInfo>(StringComparer.Ordinal);

        // Arrow keys (no modifiers).
        db["\x1b[A"] = new('\0', ConsoleKey.UpArrow, false, false, false);
        db["\x1b[B"] = new('\0', ConsoleKey.DownArrow, false, false, false);
        db["\x1b[C"] = new('\0', ConsoleKey.RightArrow, false, false, false);
        db["\x1b[D"] = new('\0', ConsoleKey.LeftArrow, false, false, false);

        // Navigation cluster.
        db["\x1b[H"] = new('\0', ConsoleKey.Home, false, false, false);
        db["\x1b[F"] = new('\0', ConsoleKey.End, false, false, false);
        db["\x1b[1~"] = new('\0', ConsoleKey.Home, false, false, false);
        db["\x1b[4~"] = new('\0', ConsoleKey.End, false, false, false);
        db["\x1b[2~"] = new('\0', ConsoleKey.Insert, false, false, false);
        db["\x1b[3~"] = new('\0', ConsoleKey.Delete, false, false, false);
        db["\x1b[5~"] = new('\0', ConsoleKey.PageUp, false, false, false);
        db["\x1b[6~"] = new('\0', ConsoleKey.PageDown, false, false, false);

        // Function keys (xterm SS3 form for F1-F4, CSI form for F5-F12).
        db["\x1bOP"] = new('\0', ConsoleKey.F1, false, false, false);
        db["\x1bOQ"] = new('\0', ConsoleKey.F2, false, false, false);
        db["\x1bOR"] = new('\0', ConsoleKey.F3, false, false, false);
        db["\x1bOS"] = new('\0', ConsoleKey.F4, false, false, false);
        db["\x1b[15~"] = new('\0', ConsoleKey.F5, false, false, false);
        db["\x1b[17~"] = new('\0', ConsoleKey.F6, false, false, false);
        db["\x1b[18~"] = new('\0', ConsoleKey.F7, false, false, false);
        db["\x1b[19~"] = new('\0', ConsoleKey.F8, false, false, false);
        db["\x1b[20~"] = new('\0', ConsoleKey.F9, false, false, false);
        db["\x1b[21~"] = new('\0', ConsoleKey.F10, false, false, false);
        db["\x1b[23~"] = new('\0', ConsoleKey.F11, false, false, false);
        db["\x1b[24~"] = new('\0', ConsoleKey.F12, false, false, false);

        return db;
    }
}
