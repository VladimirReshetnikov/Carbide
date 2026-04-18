// `carbide validate` — runs Roslyn diagnostics only, no emit or execution.

import { CarbideSession } from "@carbide/core";
import { type ParsedArgs, lastString, stringList } from "../args.js";
import { deriveAssemblyName, readReferenceBytes, readSource } from "../io.js";
import { parseFormat, renderDiagnostic, writeJson } from "../format.js";

export const VALIDATE_ARG_SPEC = {
    strings: ["source", "ref", "assembly-name", "format"],
    booleans: ["help"],
} as const;

export async function runValidate(args: ParsedArgs): Promise<number> {
    if (args.flags.has("help")) {
        process.stdout.write(VALIDATE_HELP);
        return 0;
    }

    const sources = stringList(args, "source");
    if (sources.length === 0) {
        process.stderr.write("carbide validate: at least one --source is required.\n");
        return 3;
    }
    const refs = stringList(args, "ref");
    const format = parseFormat(lastString(args, "format"));
    const assemblyName = deriveAssemblyName(lastString(args, "assembly-name"), sources);

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

Options:
  --source <path>        Source file. Repeatable. '-' reads one source from stdin.
  --ref <path>           Reference DLL. Repeatable.
  --assembly-name <n>    Assembly name. Default: basename of first source.
  --format json|human    Output format (default: json).
  --help                 Print this message.
`;
