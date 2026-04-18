// `carbide validate` — runs Roslyn diagnostics only, no emit or execution.

import path from "node:path";
import { CarbideSession, type Project } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource } from "../io.js";
import { parseFormat, renderDiagnostic, writeJson } from "../format.js";
import { runCsprojPipeline } from "../project-file.js";

export const VALIDATE_ARG_SPEC = {
    strings: ["source", "ref", "assembly-name", "format", "project"],
    booleans: ["help"],
} as const;

export async function runValidate(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(VALIDATE_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    const projectPath = lastString(args, "project");

    if (!projectPath && sources.length === 0) {
        process.stderr.write("carbide validate: provide either --project <path>.csproj or at least one --source.\n");
        return 3;
    }
    if (projectPath && (sources.length > 0 || lastString(args, "assembly-name"))) {
        process.stderr.write("carbide validate: --project is mutually exclusive with --source / --assembly-name.\n");
        return 3;
    }

    const refs = stringList(args, "ref");
    const format = parseFormat(lastString(args, "format"));

    const session = await CarbideSession.initializeAsync();
    try {
        let project: Project;
        let assemblyName: string;
        let csprojWarnings: Array<{ code: string; message: string; severity: string }> = [];

        if (projectPath) {
            const pipeline = await runCsprojPipeline(session, projectPath, refs);
            project = pipeline.project;
            const modelAsmName = pipeline.model.properties.assemblyName as string | undefined;
            assemblyName =
                modelAsmName && modelAsmName.length > 0
                    ? modelAsmName
                    : path.basename(pipeline.model.projectPath, path.extname(pipeline.model.projectPath));
            csprojWarnings = pipeline.model.warnings.map((w) => ({
                code: w.code,
                message: w.message,
                severity: w.severity,
            }));
            if (format === "human") {
                for (const w of pipeline.model.warnings) {
                    process.stderr.write(`carbide: ${w.severity} ${w.code}: ${w.message}\n`);
                }
            }
        } else {
            assemblyName = deriveAssemblyName(lastString(args, "assembly-name"), sources);
            project = session.createProject({ assemblyName });

            for (const refPath of refs) {
                const { name, bytes } = await readReferenceBytes(refPath);
                const handle = session.addReference(bytes, name);
                project.addReference(handle);
            }

            for (const sourceSpec of sources) {
                const { path: docPath, code } = await readSource(sourceSpec);
                project.addSource(docPath, code);
            }
        }

        const diagnostics = await project.getDiagnostics();
        const hasErrors = diagnostics.some((d) => d.severity === "error");

        if (format === "human") {
            for (const d of diagnostics) {
                process.stderr.write(renderDiagnostic(d) + "\n");
            }
        } else {
            writeJson({
                success: !hasErrors,
                assemblyName,
                diagnostics,
                warnings: csprojWarnings,
            });
        }
        return hasErrors ? 1 : 0;
    } finally {
        await session.shutdown();
    }
}

const VALIDATE_HELP = `\
Usage: carbide validate [options]

Run Roslyn diagnostics over the project without emitting or executing. Exit code 0 when no
error-severity diagnostics exist; non-zero otherwise.

Input modes (mutually exclusive):
  --project <path>.csproj  Parse a .csproj and validate per its options.
  --source <path>          Source file. Repeatable. '-' reads one source from stdin.

Options:
  --ref <path>           Reference DLL. Repeatable.
  --assembly-name <n>    Assembly name. Rejected when --project is used.
  --format json|human    Output format (default: json).
  --help                 Print this message.
`;
