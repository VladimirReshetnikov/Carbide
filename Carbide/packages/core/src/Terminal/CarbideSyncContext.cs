// T2 — a minimal SynchronizationContext for Mono-WASM browser. Without one installed, the
// default TaskScheduler tries to queue continuations on the thread pool (absent on
// single-threaded browser-wasm), which throws
// `PlatformNotSupportedException: Cannot wait on monitors on this runtime`.
//
// The context is installed at `ProjectCompiler.RunInteractiveAsync` entry and uninstalled
// in the finally block. It simply invokes `Post` callbacks inline (single-threaded is
// effectively the same as "same thread" on this runtime). `Send` is synchronous too.
//
// T2.1 note: a Blazor-style `RendererSynchronizationContext` clone (Task-queue-chained
// Post with `ConfigureAwaitOptions.ForceYielding`) was tried here and did NOT fix the
// Assembly.Load-plus-suspended-await trap. See
// `docs/reports/carbide-T21-dispatcher-experiment__...md` for the experimental record.

using System.Threading;

namespace Carbide.Terminal;

internal sealed class CarbideSyncContext : SynchronizationContext
{
    /// <summary>
    /// The single instance Carbide installs during an interactive run. Idempotent to
    /// create; cheap to install/uninstall.
    /// </summary>
    public static CarbideSyncContext Instance { get; } = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        // Mono-WASM browser is single-threaded. "Post" traditionally means "run the
        // callback on some other thread later"; here we run it on the same thread
        // synchronously because there is no other thread. Continuations from TCS /
        // Task.Delay / etc. see a usable scheduling path without dispatching to an
        // absent thread pool.
        d(state);
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;
}
