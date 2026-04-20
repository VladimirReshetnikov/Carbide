// T1+T2 — JSImport / JSExport surface for the interactive terminal bridge.
//
// T1 introduced WriteStdOut / WriteStdErr (C# → JS) for streaming output. T2 adds the
// return path: DeliverStdIn / NotifyResize / DeliverSignal / SetKeyMode (JS → C#) that
// route input, resize, and Ctrl+C signals into the active TerminalInputState.
//
// JSImport targets resolve against globalThis.Carbide.Terminal.*, which the TS side
// installs before calling RunInteractiveAsync and clears on dispose. JSExports are callable
// by the TS side anytime the runtime is alive; guards on each export check for the
// project's input state and silently no-op if the state isn't present (e.g. late DeliverStdIn
// after teardown).

using System.Runtime.InteropServices.JavaScript;

namespace Carbide.Terminal;

internal static partial class CarbideTerminalInterop
{
    // ---- JSImports (C# → JS) ------------------------------------------------------------

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

    /// <summary>
    /// JSImport into <c>globalThis.Carbide.Terminal.setKeyMode</c>. Called by
    /// <see cref="CarbideConsole.ReadKeyAsync"/> at await-start (true) and await-end (false)
    /// so the JS line editor knows whether to run its local-echo loop or forward raw bytes.
    /// </summary>
    [JSImport("globalThis.Carbide.Terminal.setKeyMode")]
    internal static partial void NotifyKeyMode(bool enabled);

    /// <summary>
    /// JSImport into <c>globalThis.Carbide.Terminal.setTreatControlCAsInput</c>. Called by
    /// <see cref="CarbideConsole.TreatControlCAsInput"/>'s setter; the JS side tracks the
    /// flag locally so the line editor can decide whether a <c>\x03</c> becomes a byte
    /// delivery or a signal delivery without a round-trip per keystroke.
    /// </summary>
    [JSImport("globalThis.Carbide.Terminal.setTreatControlCAsInput")]
    internal static partial void NotifyTreatControlCAsInput(bool value);

    /// <summary>
    /// T2.1 — callback-based delay. Replaces an earlier Promise-returning JSImport. The
    /// Mono-WASM Promise-to-Task marshaler forces continuations through the ThreadPool
    /// (via <c>TaskCreationOptions.RunContinuationsAsynchronously</c> on the bridging
    /// TCS it constructs in
    /// <c>JSMarshalerArgument.Task.cs:55</c>), and on single-threaded browser-wasm the
    /// ThreadPool blocks on <c>Monitor.Wait(INFINITE)</c> and trips
    /// "Cannot wait on monitors". The callback pattern lets us complete a locally-owned
    /// TCS synchronously from the setTimeout tick, so user-code awaits resume inline
    /// through <see cref="CarbideSyncContext"/> without a scheduler hop.
    /// </summary>
    [JSImport("globalThis.Carbide.Terminal.delayCallback")]
    internal static partial void DelayCallback(int milliseconds, [JSMarshalAs<JSType.Function>] Action callback);

    // ---- JSExports (JS → C#) move to Carbide.Core.CompilationInterop so the TS-side
    // `locateInterop` (which resolves `exportsRoot.Carbide.Core.CompilationInterop`) sees
    // them on the same interop object it already uses for RunAsync / BuildAsync / etc.

    // ---- Internal entry points used by CompilationInterop's JSExport wrappers -----------

    internal static void RouteStdIn(string projectId, bool isKeyMode, string data)
    {
        var state = TerminalInputState.TryGet(projectId);
        if (state is null) return;
        if (isKeyMode)
        {
            state.Reader.EnqueueRaw(data);
        }
        else
        {
            state.Reader.EnqueueLine(data);
        }
    }

    internal static void RouteResize(string projectId, int cols, int rows)
    {
        TerminalInputState.TryGet(projectId)?.ApplyResize(cols, rows);
    }

    internal static void RouteSignal(string projectId, string signalName)
    {
        var state = TerminalInputState.TryGet(projectId);
        if (state is null) return;
        if (signalName == "SIGINT")
        {
            state.FireCancelKeyPress();
        }
    }

    internal static void RouteTreatControlCAsInput(string projectId, bool value)
    {
        var state = TerminalInputState.TryGet(projectId);
        if (state is not null) state.TreatControlCAsInput = value;
    }
}
