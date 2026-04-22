// @carbide-ui/avalonia-runner — C# entry point for UI-M3.
//
// Boots into a waiting state (posts `runnerReady` to the parent window) and loads
// user PE payloads on demand via the postMessage bridge. See the plan §7.3–§7.4 and
// proposal §7.3 for the contract.
//
// v1 handles **one** user PE per iframe lifetime: the first OnLoadMessage mounts the
// Avalonia app; subsequent calls post a runnerError. The launcher's reload() handles
// re-runs by replacing iframe.src — documented in the launcher README. Full in-process
// Application swap is a v2 follow-up (proposal §12 Q.3).

using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Browser;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

namespace Carbide.UI.Runner;

public static partial class RunnerProgram
{
    private static AssemblyLoadContext? s_userContext;
    private static bool s_firstLoadDone;

    public static async Task Main()
    {
        try
        {
            // JSHost.ImportAsync runs the module's top-level code, which installs the
            // `window.addEventListener("message", ...)` listener in runner-bridge.js.
            await JSHost.ImportAsync("runner-bridge", "./runner-bridge.js").ConfigureAwait(false);
            PostReady();
            // Mono-WASM browser ends the process when Main returns. A never-completing
            // awaiter keeps the runtime alive so [JSExport]-invoked OnLoadMessage runs
            // on the main thread when a load arrives.
            await new TaskCompletionSource().Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostError($"runner boot failed: {ex}");
            throw;
        }
    }

    /// <summary>
    /// JS-side entry invoked by runner-bridge.js when a `load` message arrives from
    /// the parent frame. Loads the user's PE (and optional portable-PDB) into a per-run
    /// collectible AssemblyLoadContext, instantiates the user's <see cref="Application"/>-
    /// derived type, and starts Avalonia on the <c>out</c> div.
    /// </summary>
    [JSExport]
    public static void OnLoadMessage(string peBase64, string? pdbBase64, string appClassName)
    {
        try
        {
            if (s_firstLoadDone)
            {
                PostError("runner has already loaded a user PE in this iframe. " +
                          "Reload requires a fresh iframe (use the launcher's reload() method).");
                return;
            }
            s_firstLoadDone = true;

            var peBytes = Convert.FromBase64String(peBase64);
            var pdbBytes = pdbBase64 is null ? null : Convert.FromBase64String(pdbBase64);

            s_userContext = new AssemblyLoadContext(
                name: $"CarbideUserPE-{Guid.NewGuid():N}",
                isCollectible: true);

            Assembly userAssembly;
            using (var peStream = new MemoryStream(peBytes, writable: false))
            {
                if (pdbBytes is null)
                {
                    userAssembly = s_userContext.LoadFromStream(peStream);
                }
                else
                {
                    using var pdbStream = new MemoryStream(pdbBytes, writable: false);
                    userAssembly = s_userContext.LoadFromStream(peStream, pdbStream);
                }
            }

            var appType = userAssembly.GetType(appClassName, throwOnError: true)
                ?? throw new InvalidOperationException(
                    $"User assembly does not contain a type '{appClassName}'.");

            if (!typeof(Application).IsAssignableFrom(appType))
            {
                throw new InvalidOperationException(
                    $"User type '{appClassName}' does not derive from Avalonia.Application.");
            }

            AppBuilder.Configure(() => (Application)Activator.CreateInstance(appType)!)
                .StartBrowserAppAsync("out");

            PostRunning();
        }
        catch (Exception ex)
        {
            PostError(ex.ToString());
        }
    }

    [JSImport("postReady",   "runner-bridge")] private static partial void PostReady();
    [JSImport("postRunning", "runner-bridge")] private static partial void PostRunning();
    [JSImport("postError",   "runner-bridge")] private static partial void PostError(string message);
}
