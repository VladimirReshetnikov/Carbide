import type { CarbideInteropExports } from "./runtime/dotnet-types.js";
import type { Diagnostic, RunResult } from "./types.js";
import { parseDiagnostics, parseRunResult } from "./interop/schema.js";

export class Project {
    /** @internal */
    constructor(private readonly interop: CarbideInteropExports, public readonly id: string) {}

    addSource(path: string, code: string): void {
        this.interop.AddSource(this.id, path, code);
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
