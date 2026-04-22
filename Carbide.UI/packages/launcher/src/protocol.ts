// @carbide-ui/launcher — postMessage protocol types (plan §7.7, proposal §8).
//
// Schema version 1. Additions are forward-compatible (new optional fields, new message
// types); renames and removals are not. Both sides (@carbide-ui/avalonia-runner and
// @carbide-ui/launcher) share this version and reject messages carrying a different
// schemaVersion with a runnerError { kind: "load" }.

export const SCHEMA_VERSION = 1 as const;

export type LoadMessage = {
    readonly type: "load";
    readonly schemaVersion: typeof SCHEMA_VERSION;
    readonly peBase64: string;
    readonly pdbBase64: string | null;
    readonly appClass: string;
    readonly runArgs: readonly string[] | null;
};

export type RunnerReadyMessage = {
    readonly type: "runnerReady";
    readonly schemaVersion: typeof SCHEMA_VERSION;
};

export type RunnerRunningMessage = {
    readonly type: "runnerRunning";
    readonly schemaVersion: typeof SCHEMA_VERSION;
};

export type RunnerErrorKind = "load" | "runtime" | "teardown";

export type RunnerErrorMessage = {
    readonly type: "runnerError";
    readonly schemaVersion: typeof SCHEMA_VERSION;
    readonly message: string;
    readonly kind: RunnerErrorKind;
};

export type InboundMessage = RunnerReadyMessage | RunnerRunningMessage | RunnerErrorMessage;

export function isInboundMessage(value: unknown): value is InboundMessage {
    if (!value || typeof value !== "object") return false;
    const maybe = value as { type?: unknown; schemaVersion?: unknown };
    if (maybe.schemaVersion !== SCHEMA_VERSION) return false;
    return maybe.type === "runnerReady"
        || maybe.type === "runnerRunning"
        || maybe.type === "runnerError";
}
