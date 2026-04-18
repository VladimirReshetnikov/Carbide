// `carbide run` — compiles sources and executes the program. Streams the program's
// stdout/stderr through the outer process. Program arguments after `--` go to Main(string[]).

import { CarbideSession } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource } from "../io.js";
import { parseFormat, renderDiagnostic, writeJson } from "../format.js";

export const RUN_ARG_SPEC = {
    strings: ["source", "ref", "assembly-name", "format"],
    booleans: ["help"],
} as const;

export async function runRun(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(RUN_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    if (sources.length === 0) {
        process.stderr.write("carbide run: at least one --source is required.\n");
        return 3;
    }
    const refs = stringList(args, "ref");
    const format = parseFormat(lastString(args, "format"));
    const assemblyName = deriveAssemblyName(lastString(args, "assembly-name"), sources);
    // args.programArgs is currently unused — reserved for when ProjectCompiler.RunAsync
    // accepts program argv. See M4 plan follow-up.

    const session = await CarbideSession.initializeAsync();
    try {
        const project = session.createProject({ assemblyName });

        for (const refPath of refs) {
            const { name, bytes } = await readReferenceBytes(refPath);
            const handle = session.addReference(bytes, name);
            project.addReference(handle);
        }

        for (const sourceSpec of sources) {
            const { path: docPath, code } = await readSource(sourceSpec);
            project.addSource(docPath, code);
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
            // Runtime failure (uncaught exception). Surface stderr + carbide trailer.
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

        // Happy path.
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

Compile C# sources and execute the program. stdout/stderr from the program stream to the
outer process under --format human; under --format json (the default) they are captured
into a JSON trailer on stdout.

Options:
  --source <path>        Source file. Repeatable. '-' reads one source from stdin.
  --ref <path>           Reference DLL. Repeatable.
  --assembly-name <n>    Assembly name. Default: basename of first source.
  --format json|human    Output format (default: json).
  --help                 Print this message.
`;
