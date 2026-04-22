// @carbide-ui/launcher — UI-M0 stub. The real API ships at UI-M3; see plan §7.6.

import type { BuildResult } from "./build-result-types.js";

export type { BuildResult };

export interface LaunchOptions {
    /** Fully-qualified name of the Avalonia Application-derived class to instantiate
     *  in the runner, e.g. "MyApp.App". */
    readonly appClass: string;
    /** How long to wait for the runner to report "ready" before rejecting. Default 30s. */
    readonly readyTimeoutMs?: number;
    /** Called when the runner reports a runtime error inside the user program. */
    onRuntimeError?(message: string): void;
}

export interface LaunchHandle {
    /** Update the running UI with a new build. Safe to call repeatedly. */
    reload(build: BuildResult): Promise<void>;
    /** Tear down the runner and (optionally) remove the iframe from the DOM. */
    dispose(removeIframe?: boolean): void;
}

export async function launchInIframe(
    _build: BuildResult,
    _iframe: HTMLIFrameElement,
    _options: LaunchOptions,
): Promise<LaunchHandle> {
    throw new Error(
        "@carbide-ui/launcher: launchInIframe is a UI-M0 stub. The real implementation ships at UI-M3.",
    );
}
