// @carbide-ui/avalonia-runner — browser boot entry (UI-M3).
//
// Loads the .NET runtime, exposes Carbide.UI.Runner.RunnerProgram on globalThis so
// runner-bridge.js can dispatch incoming `load` messages into C#, then runs Main.
// Main awaits JSHost.ImportAsync("runner-bridge"), which installs the message
// listener and then calls the [JSImport]-backed PostReady.

import { dotnet } from "./_framework/dotnet.js";

const isBrowser = typeof window !== "undefined";
if (!isBrowser) {
    throw new Error("@carbide-ui/avalonia-runner expects a browser environment.");
}

const runtime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = runtime.getConfig();
const exportsRoot = await runtime.getAssemblyExports(config.mainAssemblyName);

// Navigate the namespace tree that getAssemblyExports mirrors: exports.Carbide.UI.Runner.RunnerProgram.
const runnerProgram = exportsRoot?.Carbide?.UI?.Runner?.RunnerProgram;
if (!runnerProgram || typeof runnerProgram.OnLoadMessage !== "function") {
    throw new Error(
        "@carbide-ui/avalonia-runner: Carbide.UI.Runner.RunnerProgram.OnLoadMessage " +
        "was not reachable through the assembly exports tree."
    );
}
globalThis.__carbideRunnerInterop = runnerProgram;

await runtime.runMain(config.mainAssemblyName, []);
