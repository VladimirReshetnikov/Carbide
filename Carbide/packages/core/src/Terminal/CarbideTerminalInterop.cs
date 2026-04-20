// T1 — JSImport / JSExport surface for the interactive terminal bridge.
//
// The JSImport targets resolve against globalThis.Carbide.Terminal.*, which the TS side
// installs before calling RunInteractiveAsync and clears on dispose. By construction these
// imports are never called outside an active interactive run (the only callers are the
// StreamingStdOutWriter instances installed by ProjectCompiler.RunInteractiveAsync and the
// emscripten print/printErr overlays, both of which are wired and unwired in lockstep with
// the bridge), so the JS side can rely on the bridge being live when these imports fire.

using System.Runtime.InteropServices.JavaScript;

namespace Carbide.Terminal;

internal static partial class CarbideTerminalInterop
{
    /// <summary>
    /// JSImport into <c>globalThis.Carbide.Terminal.write</c>. Called by
    /// <see cref="StreamingStdOutWriter"/>'s flush path with a batched chunk of stdout text.
    /// </summary>
    [JSImport("globalThis.Carbide.Terminal.write")]
    internal static partial void WriteStdOut(string text);

    /// <summary>
    /// JSImport into <c>globalThis.Carbide.Terminal.writeErr</c>. Called by the stderr
    /// writer's flush path with a batched chunk of stderr text. The chunk may already be
    /// wrapped in SGR escapes per the caller-chosen <see cref="StderrStyle"/>.
    /// </summary>
    [JSImport("globalThis.Carbide.Terminal.writeErr")]
    internal static partial void WriteStdErr(string text);
}
