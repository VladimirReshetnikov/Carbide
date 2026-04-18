// `carbide run` — compiles sources and executes the program. Streams the program's
// stdout/stderr through the outer process. Program arguments after `--` go to Main(string[]).

import path from "node:path";
import { CarbideSession, type Project } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource } from "../io.js";
import { parseFormat, renderDiagnostic, writeJson } from "../format.js";
import { runCsprojPipeline } from "../project-file.js";

export const RUN_ARG_SPEC = {
    strings: ["source", "ref", "assembly-name", "format", "project"],
    booleans: ["help"],
} as const;

export async function runRun(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(RUN_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    const projectPath = lastString(args, "project");

    if (!projectPath && sources.length === 0) {
        process.stderr.write("carbide run: provide either --project <path>.csproj or at least one --source.\n");
        return 3;
    }
    if (projectPath && (sources.length > 0 || lastString(args, "assembly-name"))) {
        process.stderr.write("carbide run: --project is mutually exclusive with --source / --assembly-name.\n");
        return 3;
    }

    const refs = stringList(args, "ref");
    const format = parseFormat(lastString(args, "format"));

    const session = await CarbideSession.initializeAsync();
    try {
        let project: Project;
        let assemblyName: string;

        if (projectPath) {
            const pipeline = await runCsprojPipeline(session, projectPath, refs);
            project = pipeline.project;
            const modelAsmName = pipeline.model.properties.assemblyName as string | undefined;
            assemblyName =
                modelAsmName && modelAsmName.length > 0
                    ? modelAsmName
                    : path.basename(pipeline.model.projectPath, path.extname(pipeline.model.projectPath));
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

        const result = await project.run();

        if (!result.success) {
            if (result.diagnostics.length > 0) {
                if (format === "human") {
                    for (const d of result.diagnostics) {
                        process.stderr.write(renderDiagnostic(d) + "\n");
                    }
                } else {
                    writeJson({
                        success: false,
                        assemblyName,
                        diagnostics: result.diagnostics,
                        durationMs: result.durationMs,
                    });
                }
                return 1;
            }
            process.stderr.write(result.stdErr);
            if (format === "json") {
                writeJson({
                    success: false,
                    assemblyName,
                    stdOut: result.stdOut,
                    stdErr: result.stdErr,
                    uncaughtException: result.uncaughtException ?? null,
                    exitCode: result.exitCode ?? 1,
                    durationMs: result.durationMs,
                });
            } else {
                process.stdout.write(result.stdOut);
            }
            return result.exitCode && result.exitCode !== 0 ? result.exitCode : 1;
        }

        if (format === "human") {
            process.stdout.write(result.stdOut);
            if (result.stdErr) process.stderr.write(result.stdErr);
        } else {
            writeJson({
                success: true,
                assemblyName,
                stdOut: result.stdOut,
                stdErr: result.stdErr,
                exitCode: result.exitCode ?? 0,
                durationMs: result.durationMs,
            });
        }
        return result.exitCode ?? 0;
    } finally {
        await session.shutdown();
    }
}

const RUN_HELP = `\
Usage: carbide run [options] [-- <program args>...]

Compile C# sources and execute the program.

Input modes (mutually exclusive):
  --project <path>.csproj  Parse a .csproj and run per its options.
  --source <path>          Source file. Repeatable. '-' reads one source from stdin.

Options:
  --ref <path>           Reference DLL. Repeatable.
  --assembly-name <n>    Assembly name. Rejected when --project is used.
  --format json|human    Output format (default: json).
  --help                 Print this message.
`;
