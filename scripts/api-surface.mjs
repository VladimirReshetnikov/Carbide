// SPDX-License-Identifier: Apache-2.0
//
// M7 — public API surface extractor and compatibility-freeze gate.
//
// Renders the exported TypeScript surface of every publishable Carbide package into a
// deterministic markdown report under `api/`. The reports are committed; CI regenerates
// them and fails when the working tree disagrees, so a change to a published type or to a
// CLI flag set can never land without showing up as a reviewable API diff.
//
// Usage:
//   node scripts/api-surface.mjs            # check committed reports (exit 1 on drift)
//   node scripts/api-surface.mjs --write    # regenerate the committed reports
//
// The extractor reads the *built* `.d.ts` files rather than the `.ts` sources: the
// declaration output is what consumers actually compile against, and it resolves `as const`
// literal types (the CLI's arg specs spread shared flag tuples, so only the emitted
// declaration lists the real flag set).

import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
/** @type {import("typescript")} */
const ts = require("typescript");

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const apiDirectory = path.join(repositoryRoot, "api");

/**
 * The frozen surfaces. `entries` are package-relative paths to emitted declaration files;
 * every export of each entry is rendered, in name order.
 *
 * Library packages list their `exports` subpaths. `@carbide/cli` publishes a binary rather
 * than a module graph, so its surface is the contract a caller can actually depend on: the
 * per-command flag specs, the exit-code taxonomy, and the structured error categories.
 */
const surfaces = [
    {
        id: "carbide-core",
        title: "@carbide/core",
        packageDirectory: "Carbide/packages/core",
        summary: "Runtime, session, project, and terminal surface. The browser and Node hosts share it.",
        entries: [
            { label: "@carbide/core", file: "dist/index.d.ts" },
            { label: "@carbide/core/node", file: "dist/node.d.ts" },
            { label: "@carbide/core/interop/schema", file: "dist/interop/schema.d.ts" },
        ],
    },
    {
        id: "carbide-msbuild-lite",
        title: "@carbide/msbuild-lite",
        packageDirectory: "Carbide/packages/msbuild-lite",
        summary: "Bounded MSBuild evaluator: `.csproj` parsing, property evaluation, compile-item globbing.",
        entries: [{ label: "@carbide/msbuild-lite", file: "dist/index.d.ts" }],
    },
    {
        id: "carbide-nuget",
        title: "@carbide/nuget",
        packageDirectory: "Carbide/packages/nuget",
        summary: "Bounded NuGet v3 resolver: allow-list policy, TFM compatibility, cache, and lock file.",
        entries: [{ label: "@carbide/nuget", file: "dist/index.d.ts" }],
    },
    {
        id: "carbide-cli",
        title: "@carbide/cli",
        packageDirectory: "Carbide/packages/cli",
        summary:
            "The `carbide` binary. Its stable contract is the command/flag surface, the exit-code " +
            "taxonomy, and the structured error categories — not the module graph behind them.",
        entries: [
            { label: "carbide — argument grammar", file: "dist/args.d.ts" },
            { label: "carbide — output formats", file: "dist/format.d.ts" },
            { label: "carbide — exit codes and error categories", file: "dist/errors.d.ts" },
            { label: "carbide — logging flags", file: "dist/logging.d.ts" },
            { label: "carbide — NuGet flags", file: "dist/nuget-options.d.ts" },
            { label: "carbide build", file: "dist/commands/build.d.ts" },
            { label: "carbide run", file: "dist/commands/run.d.ts" },
            { label: "carbide validate", file: "dist/commands/validate.d.ts" },
            { label: "carbide audit", file: "dist/commands/audit.d.ts" },
            { label: "carbide tree", file: "dist/commands/tree.d.ts" },
        ],
    },
];

const compilerOptions = {
    target: ts.ScriptTarget.ES2022,
    module: ts.ModuleKind.ES2022,
    moduleResolution: ts.ModuleResolutionKind.Bundler,
    strict: true,
    skipLibCheck: true,
    noEmit: true,
    lib: ["lib.es2022.d.ts", "lib.dom.d.ts"],
    types: ["node"],
    resolveJsonModule: true,
};

const printer = ts.createPrinter({ removeComments: true, newLine: ts.NewLineKind.LineFeed });

/** Map a declaration onto the node whose printed form carries its modifiers. */
function declarationStatement(declaration) {
    if (ts.isVariableDeclaration(declaration)) {
        // VariableDeclaration → VariableDeclarationList → VariableStatement.
        return declaration.parent.parent;
    }
    return declaration;
}

/**
 * Private members appear in the emitted `.d.ts` but are unreachable from a consumer, so
 * renaming one is not an API change. Drop them; keep a `private constructor()` because its
 * visibility *is* contract (it makes the class non-constructible from outside).
 */
function isHiddenClassMember(member) {
    if (ts.isConstructorDeclaration(member)) {
        return false;
    }
    if (member.name && ts.isPrivateIdentifier(member.name)) {
        return true;
    }
    const modifiers = ts.canHaveModifiers(member) ? (ts.getModifiers(member) ?? []) : [];
    return modifiers.some((modifier) => modifier.kind === ts.SyntaxKind.PrivateKeyword);
}

function withoutPrivateMembers(declaration) {
    if (!ts.isClassDeclaration(declaration)) {
        return declaration;
    }
    const members = declaration.members.filter((member) => !isHiddenClassMember(member));
    if (members.length === declaration.members.length) {
        return declaration;
    }
    return ts.factory.createClassDeclaration(
        declaration.modifiers,
        declaration.name,
        declaration.typeParameters,
        declaration.heritageClauses,
        members,
    );
}

/** Declarations that only exist to re-export something else carry no shape of their own. */
function isPassThroughDeclaration(declaration) {
    return (
        ts.isExportSpecifier(declaration) ||
        ts.isImportSpecifier(declaration) ||
        ts.isImportClause(declaration) ||
        ts.isNamespaceImport(declaration) ||
        ts.isExportAssignment(declaration)
    );
}

function isDeprecated(declarations) {
    return declarations.some((declaration) =>
        ts.getJSDocTags(declaration).some((tag) => tag.tagName.escapedText === "deprecated"),
    );
}

/**
 * Render one module's exports. Aliases are resolved to the declaration they point at, so a
 * barrel `export { X } from "./x.js"` prints X's real shape.
 */
function renderModule(program, checker, sourceFile) {
    const moduleSymbol = checker.getSymbolAtLocation(sourceFile);
    if (!moduleSymbol) {
        throw new Error(`${sourceFile.fileName}: not a module (no export list to freeze).`);
    }

    const rendered = [];
    for (const exported of checker.getExportsOfModule(moduleSymbol)) {
        const name = exported.getName();
        const target =
            exported.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(exported) : exported;
        const declarations = (target.getDeclarations() ?? []).filter(
            (declaration) => !isPassThroughDeclaration(declaration),
        );
        if (declarations.length === 0) {
            rendered.push({ name, text: `// ${name}: re-exported symbol with no local declaration` });
            continue;
        }

        const lines = [];
        if (isDeprecated(declarations)) {
            lines.push(`/** @deprecated */`);
        }
        if (target.getName() !== name) {
            lines.push(`// exported as \`${name}\` (declared as \`${target.getName()}\`)`);
        }
        for (const declaration of declarations) {
            const statement = withoutPrivateMembers(declarationStatement(declaration));
            lines.push(printer.printNode(ts.EmitHint.Unspecified, statement, declaration.getSourceFile()));
        }
        rendered.push({ name, text: lines.join("\n") });
    }

    rendered.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
    return rendered;
}

function buildSurfaceReport(surface) {
    const packageRoot = path.join(repositoryRoot, surface.packageDirectory);
    const manifest = JSON.parse(readFileSync(path.join(packageRoot, "package.json"), "utf8"));

    const rootFiles = surface.entries.map((entry) => path.join(packageRoot, entry.file));
    const missing = rootFiles.filter((file) => !existsSync(file));
    if (missing.length > 0) {
        const relative = missing.map((file) => path.relative(repositoryRoot, file).replaceAll("\\", "/"));
        throw new Error(
            `${surface.title}: declaration output is missing — run \`npm run build\` in ` +
                `${surface.packageDirectory} first.\n  ${relative.join("\n  ")}`,
        );
    }

    const program = ts.createProgram(rootFiles, compilerOptions);
    const checker = program.getTypeChecker();

    const sections = [];
    for (const entry of surface.entries) {
        const absolute = path.join(packageRoot, entry.file);
        const sourceFile = program.getSourceFile(absolute);
        if (!sourceFile) {
            throw new Error(`${surface.title}: TypeScript did not load ${entry.file}.`);
        }
        const exports = renderModule(program, checker, sourceFile);
        sections.push({ entry, exports });
    }

    const out = [];
    out.push(`# ${surface.title} — public API surface`);
    out.push("");
    out.push(`<!-- Generated by scripts/api-surface.mjs. Do not edit by hand. -->`);
    out.push("");
    out.push(surface.summary);
    out.push("");
    out.push(`Frozen at version \`${manifest.version}\`.`);
    out.push("");
    for (const section of sections) {
        out.push(`## \`${section.entry.label}\``);
        out.push("");
        if (section.exports.length === 0) {
            out.push("_No exports._");
            out.push("");
            continue;
        }
        out.push("```ts");
        out.push(section.exports.map((item) => item.text).join("\n\n"));
        out.push("```");
        out.push("");
    }
    return out.join("\n");
}

/** Line-level diff (LCS) — small enough for report-sized inputs, and dependency-free. */
function diffLines(expected, actual) {
    const a = expected.split("\n");
    const b = actual.split("\n");
    const lengths = Array.from({ length: a.length + 1 }, () => new Uint32Array(b.length + 1));
    for (let i = a.length - 1; i >= 0; i--) {
        for (let j = b.length - 1; j >= 0; j--) {
            lengths[i][j] = a[i] === b[j] ? lengths[i + 1][j + 1] + 1 : Math.max(lengths[i + 1][j], lengths[i][j + 1]);
        }
    }
    const out = [];
    let i = 0;
    let j = 0;
    while (i < a.length && j < b.length) {
        if (a[i] === b[j]) {
            out.push(`  ${a[i]}`);
            i++;
            j++;
        } else if (lengths[i + 1][j] >= lengths[i][j + 1]) {
            out.push(`- ${a[i++]}`);
        } else {
            out.push(`+ ${b[j++]}`);
        }
    }
    while (i < a.length) out.push(`- ${a[i++]}`);
    while (j < b.length) out.push(`+ ${b[j++]}`);
    // Collapse long unchanged runs so the failure output stays readable.
    const context = 3;
    const keep = new Array(out.length).fill(false);
    out.forEach((line, index) => {
        if (line.startsWith("- ") || line.startsWith("+ ")) {
            for (let k = Math.max(0, index - context); k <= Math.min(out.length - 1, index + context); k++) {
                keep[k] = true;
            }
        }
    });
    const collapsed = [];
    let skipping = false;
    out.forEach((line, index) => {
        if (keep[index]) {
            collapsed.push(line);
            skipping = false;
        } else if (!skipping) {
            collapsed.push("  ...");
            skipping = true;
        }
    });
    return collapsed.join("\n");
}

const write = process.argv.includes("--write");
const reportPathFor = (surface) => path.join(apiDirectory, `${surface.id}.api.md`);
const failures = [];

mkdirSync(apiDirectory, { recursive: true });

for (const surface of surfaces) {
    const reportPath = reportPathFor(surface);
    const relativeReport = path.relative(repositoryRoot, reportPath).replaceAll("\\", "/");
    let actual;
    try {
        actual = buildSurfaceReport(surface);
    } catch (error) {
        failures.push(`${surface.title}: ${error.message}`);
        continue;
    }

    if (write) {
        writeFileSync(reportPath, actual, "utf8");
        console.log(`wrote ${relativeReport}`);
        continue;
    }

    if (!existsSync(reportPath)) {
        failures.push(`${relativeReport}: report is missing — run \`node scripts/api-surface.mjs --write\`.`);
        continue;
    }
    const expected = readFileSync(reportPath, "utf8").replaceAll("\r\n", "\n");
    if (expected !== actual) {
        failures.push(
            `${relativeReport}: public API surface changed.\n${diffLines(expected, actual)}\n` +
                `  (\`-\` committed, \`+\` current. Re-run \`node scripts/api-surface.mjs --write\` and ` +
                `record the change in the package CHANGELOG.)`,
        );
    }
}

// A surface that stops being extracted must not silently drop its report.
const knownReports = new Set(surfaces.map((surface) => `${surface.id}.api.md`));
for (const name of existsSync(apiDirectory) ? readdirSync(apiDirectory) : []) {
    if (name.endsWith(".api.md") && !knownReports.has(name)) {
        failures.push(`api/${name}: orphaned report — no surface in scripts/api-surface.mjs produces it.`);
    }
}

if (failures.length > 0) {
    console.error("API surface check failed:\n");
    for (const failure of failures) {
        console.error(`- ${failure}\n`);
    }
    process.exitCode = 1;
} else if (!write) {
    const entryCount = surfaces.reduce((total, surface) => total + surface.entries.length, 0);
    console.log(
        `API surface check passed: ${surfaces.length} packages, ${entryCount} entry points match their committed reports.`,
    );
}
