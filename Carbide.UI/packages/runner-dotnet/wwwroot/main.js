// @carbide-ui/avalonia-runner — browser boot entry (UI-M2).
// Loads the .NET runtime from _framework/, then invokes the runner's Main.
// At UI-M3 this file also imports runner-bridge.js to wire the postMessage
// protocol with the launcher frame.

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
await runtime.runMain(config.mainAssemblyName, []);
