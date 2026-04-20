import type { CarbideInteropExports } from "./runtime/dotnet-types.js";
import type {
    BuildResult,
    Diagnostic,
    InteractiveRunOptions,
    ReferenceHandle,
    RunOptions,
    RunResult,
    TerminalSession,
} from "./types.js";
import { parseBuildResult, parseDiagnostics, parseRunResult, SCHEMA_VERSION } from "./interop/schema.js";
import type { HostAdapter } from "./host/adapter.js";
import type { BrowserHostAdapter } from "./host/browser/browser-adapter.js";
import { startInteractiveSession } from "./terminal/session.js";

export class Project {
    /**
     * Tracks the in-flight interactive session on this project, if any. Guards re-entrant
     * `runInteractive` — a second call while one is live throws instead of silently racing.
     * Cleared when `exitPromise` resolves (success or failure).
     */
    private _activeInteractive: TerminalSession | null = null;

    /** @internal */
    constructor(
        private readonly interop: CarbideInteropExports,
        public readonly id: string,
        /** @internal */ readonly sessionId: string,
        /** @internal */ readonly adapter: HostAdapter,
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

    /**
     * Compiles the project and returns the emitted PE (and portable-PDB) bytes without
     * executing anything. On compile failure the `diagnostics` array carries the errors and
     * `pe` / `pdb` are absent. Use {@link run} to both compile and execute.
     */
    async build(): Promise<BuildResult> {
        const json = await this.interop.BuildAsync(this.id);
        return parseBuildResult(json);
    }

    /**
     * Compile the project and execute the program.
     *
     * U2: optional {@link RunOptions} forward program arguments to the entry point's
     * `Main(string[] args)` parameter (when it has one) and pre-seed `Console.In` with
     * a string for programs that read stdin. Omitting the options parameter preserves the
     * pre-U2 behaviour (empty args, disconnected stdin) and skips JSON marshalling on the
     * interop boundary.
     */
    async run(options?: RunOptions): Promise<RunResult> {
        const optionsJson = serializeRunOptions(options);
        const json = await this.interop.RunAsync(this.id, optionsJson);
        return parseRunResult(json);
    }

    /**
     * T1 — compile and run the project interactively, streaming stdout/stderr into the
     * xterm.js `Terminal` supplied in {@link InteractiveRunOptions.terminal}. Returns a
     * {@link TerminalSession} handle; `await session.exitPromise` waits for the program to
     * exit.
     *
     * Only available on browser-backed sessions; throws synchronously on Node. Only one
     * interactive run per project at a time; a second concurrent call throws.
     */
    runInteractive(options: InteractiveRunOptions): TerminalSession {
        if (this.adapter.hostKind !== "browser") {
            throw new Error(
                "Project.runInteractive: interactive terminals require the browser host adapter " +
                    `(got hostKind='${this.adapter.hostKind}').`,
            );
        }
        if (this._activeInteractive) {
            throw new Error(
                `Project.runInteractive: project ${this.id} already has an active interactive session. ` +
                    "Await its exitPromise or call session.dispose() before starting another.",
            );
        }
        if (!options || !options.terminal) {
            throw new Error("Project.runInteractive: options.terminal is required.");
        }

        const session = startInteractiveSession(
            this.interop,
            this.id,
            this.adapter as BrowserHostAdapter,
            options,
        );
        this._activeInteractive = session;
        // Clear the slot when the run ends so a subsequent call can proceed. Use .finally
        // rather than .then to cover both the success and failure branches; exitPromise is
        // contractually never-reject so the `catch` variant isn't strictly needed.
        void session.exitPromise.finally(() => {
            if (this._activeInteractive === session) {
                this._activeInteractive = null;
            }
        });
        return session;
    }
}

function serializeRunOptions(options: RunOptions | undefined): string {
    if (!options) return "";
    const hasArgs = options.args && options.args.length > 0;
    const hasStdin = options.stdin !== undefined && options.stdin !== null;
    if (!hasArgs && !hasStdin) return "";
    const payload: { schemaVersion: number; args?: readonly string[]; stdin?: string | null } = {
        schemaVersion: SCHEMA_VERSION,
    };
    if (hasArgs) payload.args = options.args;
    if (hasStdin) payload.stdin = options.stdin;
    return JSON.stringify(payload);
}
