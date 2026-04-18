import type { CarbideInteropExports } from "./runtime/dotnet-types.js";
import type { Diagnostic, ReferenceHandle, RunResult } from "./types.js";
import { parseDiagnostics, parseRunResult } from "./interop/schema.js";

export class Project {
    /** @internal */
    constructor(
        private readonly interop: CarbideInteropExports,
        public readonly id: string,
        /** @internal */ readonly sessionId: string,
    ) {}

    /**
     * Adds a new source file to the project. Throws if a document at this exact path already
     * exists — use {@link updateSource} to replace its content. Paths are compared byte-for-byte;
     * casing and slash direction matter.
     */
    addSource(path: string, code: string): void {
        this.interop.AddSource(this.id, path, code);
    }

    /**
     * Replaces the content of an existing source file. Throws if no document has been added
     * at this exact path.
     */
    updateSource(path: string, code: string): void {
        this.interop.UpdateSource(this.id, path, code);
    }

    /**
     * Removes a source file from the project. No-op if the path was never added — teardown
     * code can tolerate partial state without a try/catch dance.
     */
    removeSource(path: string): void {
        this.interop.RemoveSource(this.id, path);
    }

    /**
     * Attaches a session-registered reference to this project. Subsequent compilations see
     * the reference's metadata. Idempotent. The handle must belong to the same session that
     * created this project.
     */
    addReference(handle: ReferenceHandle): void {
        if (handle.sessionId !== this.sessionId) {
            throw new Error(
                `Reference handle '${handle.id}' belongs to session '${handle.sessionId}', ` +
                    `not this project's session '${this.sessionId}'. ` +
                    "References are session-scoped; cross-session attach is not allowed.",
            );
        }
        if (handle.disposed) {
            throw new Error(
                `Reference handle '${handle.id}' has been disposed (removeReference or session shutdown).`,
            );
        }
        this.interop.AttachReference(this.id, handle.id);
    }

    async getDiagnostics(): Promise<Diagnostic[]> {
        const json = await this.interop.GetDiagnosticsAsync(this.id);
        return parseDiagnostics(json);
    }

    async run(): Promise<RunResult> {
        const json = await this.interop.RunAsync(this.id);
        return parseRunResult(json);
    }
}
