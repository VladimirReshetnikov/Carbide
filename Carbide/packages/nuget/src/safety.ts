// Safety refusals — reject packages carrying contents Carbide's runtime cannot consume
// (native binaries, MSBuild .targets). Applied at resolve time so bad bytes never reach the
// Roslyn reference registry.
//
// Roslyn analyzers used to be refused here too; since M12 they are selected as assets
// instead. See `analyzer-assets.ts`.

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
        // Roslyn analyzers are no longer a refusal — Carbide runs source generators (M12), so
        // the assets are selected by `selectAnalyzerAssets` and attached to the compilation.
        // What that selector cannot place is reported as an MSNUGET017 warning by the caller
        // rather than taking the whole package down: the package's `lib/` assets are still
        // perfectly usable, and refusing them over an analyzer we did not recognise blocks
        // work that would otherwise succeed.
    }
    return { kind: "ok" };
}

function refused(code: string, message: string, offendingEntry: string): SafetyResult {
    return { kind: "refused", code, message, offendingEntry };
}
