// T2 — per-run bag carrying the interactive session's input-side state: the line reader,
// cached window size, Ctrl+C policy + handlers, and the run-level cancellation token that
// `CancelKeyPress` trips. Looked up by projectId from a static registry so JSExports can
// route incoming DeliverStdIn / NotifyResize / DeliverSignal calls to the right run.

using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace Carbide.Terminal;

internal sealed class TerminalInputState
{
    private static readonly ConcurrentDictionary<string, TerminalInputState> s_registry = new(StringComparer.Ordinal);

    /// <summary>
    /// Thread-static shortcut to the currently-running state. Set by
    /// <see cref="Carbide.Core.Services.ProjectCompiler.RunInteractiveAsync"/> on entry and
    /// cleared in its finally block. Mono-WASM is single-threaded, so a plain static is
    /// sufficient; ThreadStatic is used defensively in case host paths ever run the compiler
    /// on a non-main thread.
    /// </summary>
    [ThreadStatic]
    private static TerminalInputState? s_current;

    public string ProjectId { get; }
    public BrowserTerminalReader Reader { get; } = new();
    public CancellationTokenSource CancellationTokenSource { get; } = new();

    /// <summary>
    /// Cached terminal geometry. Initialized to (80, 24) and refreshed by every
    /// <see cref="CarbideTerminalInterop.NotifyResize"/> call.
    /// </summary>
    public int Cols { get; private set; } = 80;

    /// <summary>Cached terminal rows.</summary>
    public int Rows { get; private set; } = 24;

    /// <summary>
    /// Fires on every <see cref="CarbideTerminalInterop.NotifyResize"/> call, after
    /// <see cref="Cols"/>/<see cref="Rows"/> have been updated.
    /// </summary>
    public event EventHandler<(int Cols, int Rows)>? Resized;

    /// <summary>
    /// Ctrl+C input policy. When <c>false</c> (default), a <c>\x03</c> byte delivered via
    /// DeliverSignal fires <see cref="CancelKeyPress"/> and trips
    /// <see cref="CancellationTokenSource"/>. When <c>true</c>, the JS side passes the byte
    /// through as stdin instead. Matches the semantics of <c>Console.TreatControlCAsInput</c>.
    /// </summary>
    public bool TreatControlCAsInput { get; set; }

    /// <summary>
    /// Fired when the JS side reports SIGINT (Ctrl+C) and <see cref="TreatControlCAsInput"/>
    /// is <c>false</c>. Matches the <see cref="Console.CancelKeyPress"/> contract — handlers
    /// run synchronously on the delivery path; setting <c>args.Cancel = true</c> suppresses
    /// the subsequent cancellation-token trip.
    /// </summary>
    public event ConsoleCancelEventHandler? CancelKeyPress;

    /// <summary>Add handler. Called by <see cref="CarbideConsole.CancelKeyPress"/>.</summary>
    internal void AddCancelKeyPressHandler(ConsoleCancelEventHandler? handler) => CancelKeyPress += handler;

    /// <summary>Remove handler.</summary>
    internal void RemoveCancelKeyPressHandler(ConsoleCancelEventHandler? handler) => CancelKeyPress -= handler;

    /// <summary>
    /// Cached foreground color so <see cref="CarbideConsole.ForegroundColor"/> getter has a
    /// sensible value without a terminal round-trip.
    /// </summary>
    public ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;

    /// <summary>Cached background color.</summary>
    public ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;

    /// <summary>Cached cursor-visible state (DECTCEM).</summary>
    public bool CursorVisible { get; set; } = true;

    public TerminalInputState(string projectId)
    {
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
    }

    /// <summary>Register state in the global lookup and set the thread-static current.</summary>
    public static TerminalInputState Create(string projectId)
    {
        var state = new TerminalInputState(projectId);
        s_registry[projectId] = state;
        s_current = state;
        return state;
    }

    /// <summary>Release the state from the global lookup and clear the thread-static.</summary>
    public void Dispose()
    {
        s_registry.TryRemove(ProjectId, out _);
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
        Reader.Complete();
        CancellationTokenSource.Dispose();
    }

    /// <summary>Lookup state by projectId. Returns null when no interactive session is live.</summary>
    public static TerminalInputState? TryGet(string projectId)
    {
        return s_registry.TryGetValue(projectId, out var state) ? state : null;
    }

    /// <summary>
    /// The currently-running state on this thread. Used by <see cref="CarbideConsole"/>'s
    /// static members so user code doesn't need a session handle threaded through every call.
    /// </summary>
    public static TerminalInputState? Current => s_current;

    /// <summary>Invoked by <see cref="CarbideTerminalInterop.NotifyResize"/>.</summary>
    internal void ApplyResize(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        Resized?.Invoke(this, (cols, rows));
    }

    /// <summary>
    /// Invoked by <see cref="CarbideTerminalInterop.DeliverSignal"/> with <c>"SIGINT"</c>.
    /// Fires the <see cref="CancelKeyPress"/> chain, then (unless a handler set
    /// <c>args.Cancel = true</c>) trips <see cref="CancellationTokenSource"/>.
    /// </summary>
    internal void FireCancelKeyPress()
    {
        // ConsoleCancelEventArgs's constructor is `internal`; reach it via reflection so
        // our event payload matches the BCL type user code expects. Fallback to an
        // uninitialized instance when the reflected constructor isn't reachable (trimmed
        // or moved in a future BCL refresh). Cached after first resolution.
        var args = CreateCancelEventArgs();
        try
        {
            CancelKeyPress?.Invoke(null, args);
        }
#pragma warning disable CA1031 // handler exceptions shouldn't take down the run
        catch
#pragma warning restore CA1031
        {
            // Swallow — a thrown handler shouldn't prevent token cancellation.
        }

        // T3 — also fan out to the static `Console.CancelKeyPress` chain so user code that
        // attaches to the idiomatic BCL event gets the callback. Only present when the T3
        // forked System.Console.dll is loaded; stock BCL has no such method and the
        // reflected MethodInfo stays null.
        var forkCancelled = InvokeForkedConsoleCancelKeyPress();

        if ((args is null || !args.Cancel) && !forkCancelled)
        {
            CancellationTokenSource.Cancel();
        }
    }

    private static MethodInfo? s_forkedHandleCancelKeyPress;
    private static bool s_forkedHandleProbed;

    private static bool InvokeForkedConsoleCancelKeyPress()
    {
        if (!s_forkedHandleProbed)
        {
            try
            {
                s_forkedHandleCancelKeyPress = typeof(Console).GetMethod(
                    "HandleCancelKeyPress",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    binder: null,
                    types: [typeof(ConsoleSpecialKey)],
                    modifiers: null);
            }
#pragma warning disable CA1031
            catch
#pragma warning restore CA1031
            {
                s_forkedHandleCancelKeyPress = null;
            }
            s_forkedHandleProbed = true;
        }
        if (s_forkedHandleCancelKeyPress is null)
        {
            return false;
        }
        try
        {
            var result = s_forkedHandleCancelKeyPress.Invoke(null, [ConsoleSpecialKey.ControlC]);
            return result is true;
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            return false;
        }
    }

    // Review R1 m2 removed two dead "cached ctor probe" fields that were never set and
    // only existed to suppress unused-variable warnings. CreateCancelEventArgs reflects
    // every call; for a Ctrl+C path that fires at most once per interactive run this is
    // not worth caching — if that changes, reintroduce a lazy `Lazy<ConstructorInfo?>`
    // or a compiled-delegate path here rather than raw unused fields.
    private static ConsoleCancelEventArgs? CreateCancelEventArgs()
    {
        // Prefer the reflected internal ctor; fall back to GetUninitializedObject so the
        // event handler always sees a usable args object even if the ctor lookup fails.
        try
        {
            var ctor = typeof(ConsoleCancelEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(ConsoleSpecialKey)],
                modifiers: null);
            if (ctor is not null)
            {
                return (ConsoleCancelEventArgs)ctor.Invoke([ConsoleSpecialKey.ControlC]);
            }
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
        }

        // Fallback: `RuntimeHelpers.GetUninitializedObject` bypasses the ctor entirely.
        // The resulting object has Cancel=false (default) and SpecialKey=0 which parses
        // as ConsoleSpecialKey.ControlC, which is what we want.
        try
        {
            return (ConsoleCancelEventArgs)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(ConsoleCancelEventArgs));
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
