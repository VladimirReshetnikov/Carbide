// Safety refusals — reject packages carrying contents Carbide's runtime cannot safely consume
// (native binaries, MSBuild .targets, analyzers, source generators). Applied at resolve time
// so bad bytes never reach the Roslyn reference registry.

import { MSNUGET_CODES } from "./warnings.js";

export type SafetyResult =
    | { kind: "ok" }
    | { kind: "refused"; code: string; message: string; offendingEntry: string };

export function checkSafety(
    id: string,
    version: string,
    entries: readonly string[],
): SafetyResult {
    for (const raw of entries) {
        const entry = raw.replace(/\\/g, "/").toLowerCase();
        // Native binaries: runtimes/<rid>/native/.
        if (/^runtimes\/[^/]+\/native\//.test(entry)) {
            return refused(
                MSNUGET_CODES.SAFETY_NATIVE,
                `Package '${id}' (${version}) carries native binaries at '${raw}'. Carbide's Mono-WASM runtime cannot load them.`,
                raw,
            );
        }
        // MSBuild targets (both build/ and buildTransitive/).
        if (/^build(transitive)?\/[^/]+\.targets$/i.test(entry) || /^build(transitive)?\/[^/]+\.props$/i.test(entry)) {
            return refused(
                MSNUGET_CODES.SAFETY_TARGETS,
                `Package '${id}' (${version}) carries an MSBuild .targets/.props file at '${raw}'. Carbide does not execute MSBuild tasks.`,
                raw,
            );
        }
        // Roslyn analyzers.
        if (/^analyzers\//.test(entry)) {
            return refused(
                MSNUGET_CODES.SAFETY_ANALYZERS,
                `Package '${id}' (${version}) contains a Roslyn analyzer at '${raw}'. Analyzer execution lands in a later milestone.`,
                raw,
            );
        }
    }
    return { kind: "ok" };
}

function refused(code: string, message: string, offendingEntry: string): SafetyResult {
    return { kind: "refused", code, message, offendingEntry };
}
