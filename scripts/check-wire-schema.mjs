// SPDX-License-Identifier: Apache-2.0
//
// M7 — cross-language wire-contract gate.
//
// `@carbide/core` marshals every JSExport call as JSON. The shape lives twice: once as a
// TypeScript interface in `src/ts/interop/schema.ts` (or `src/ts/types.ts`), once as a C#
// DTO in `src/CompilationInterop.cs` (or `src/Services/*.cs`). Nothing at build time links
// the two — TypeScript never sees the C# and the C# never sees the TypeScript — so a field
// added on one side silently becomes `undefined` on the other.
//
// This script closes that gap by comparing the declared field sets and the schema-version
// constants directly. It is a static check: no toolchain, no runtime, no build output.
//
//   node scripts/check-wire-schema.mjs

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const coreRoot = path.join(repositoryRoot, "Carbide/packages/core");
const errors = [];

function read(relativeToCore) {
    return readFileSync(path.join(coreRoot, relativeToCore), "utf8");
}

function assert(condition, message) {
    if (!condition) {
        errors.push(message);
    }
}

const schemaTs = read("src/ts/interop/schema.ts");
const typesTs = read("src/ts/types.ts");
const interopCs = read("src/CompilationInterop.cs");
const buildResultCs = read("src/Services/BuildResult.cs");
const runResultCs = read("src/Services/RunResult.cs");
const diagnosticCs = read("src/Services/Diagnostic.cs");

// ---------------------------------------------------------------------------
// 1. SCHEMA_VERSION parity.
// ---------------------------------------------------------------------------

const schemaVersionMatch = /export const SCHEMA_VERSION = (\d+) as const;/.exec(schemaTs);
if (!schemaVersionMatch) {
    errors.push("src/ts/interop/schema.ts: could not find `export const SCHEMA_VERSION = <n> as const;`");
}
const schemaVersion = schemaVersionMatch ? Number(schemaVersionMatch[1]) : Number.NaN;

// The C# validator lists every version it tolerates inbound. Its ceiling must be the
// TypeScript SCHEMA_VERSION: a lower ceiling rejects current clients, a higher one accepts
// payloads no released TypeScript can produce.
const validatorMatch = /if \(schemaVersion is not null(?<clauses>(?: and not \d+)+)\)/.exec(interopCs);
if (!validatorMatch) {
    errors.push("src/CompilationInterop.cs: could not find the ValidateSchemaVersion accept-list");
} else {
    const accepted = [...validatorMatch.groups.clauses.matchAll(/and not (\d+)/g)].map((m) => Number(m[1]));
    const ceiling = Math.max(...accepted);
    assert(
        ceiling === schemaVersion,
        `ValidateSchemaVersion accepts up to ${ceiling} but SCHEMA_VERSION is ${schemaVersion} — ` +
            "bump both sides in lock-step",
    );
    const expectedRange = Array.from({ length: schemaVersion }, (_, index) => index + 1);
    assert(
        JSON.stringify([...accepted].sort((a, b) => a - b)) === JSON.stringify(expectedRange),
        `ValidateSchemaVersion should accept 1..${schemaVersion} contiguously, got [${accepted.join(", ")}]`,
    );
}

// Outbound C# payloads stamp the current version; a stale literal would make every response
// look like a back-version to the TypeScript parsers.
for (const [file, source] of [
    ["src/Services/BuildResult.cs", buildResultCs],
    ["src/Services/RunResult.cs", runResultCs],
]) {
    const match = /public int SchemaVersion \{ get; init; \} = (\d+);/.exec(source);
    if (!match) {
        errors.push(`${file}: could not find the SchemaVersion initializer`);
        continue;
    }
    assert(
        Number(match[1]) === schemaVersion,
        `${file}: stamps schemaVersion ${match[1]}, but SCHEMA_VERSION is ${schemaVersion}`,
    );
}

const buildResultDtoVersion = /internal sealed class BuildResultDto\b[\s\S]*?public int SchemaVersion \{ get; set; \} = (\d+);/.exec(interopCs);
if (!buildResultDtoVersion) {
    errors.push("src/CompilationInterop.cs: could not find BuildResultDto.SchemaVersion");
} else {
    assert(
        Number(buildResultDtoVersion[1]) === schemaVersion,
        `BuildResultDto stamps schemaVersion ${buildResultDtoVersion[1]}, but SCHEMA_VERSION is ${schemaVersion}`,
    );
}

// ---------------------------------------------------------------------------
// 2. Field-set parity.
// ---------------------------------------------------------------------------

/** Field names of a TypeScript interface, in declaration order. */
function tsInterfaceFields(source, name, file) {
    const header = new RegExp(`(?:export )?interface ${name}\\s*\\{`).exec(source);
    if (!header) {
        errors.push(`${file}: interface ${name} not found`);
        return null;
    }
    const body = balancedBody(source, header.index + header[0].length - 1);
    if (body === null) {
        errors.push(`${file}: interface ${name} has an unbalanced body`);
        return null;
    }
    return [...stripComments(body).matchAll(/^\s*(?:readonly\s+)?([A-Za-z_$][\w$]*)\??\s*:/gm)].map((m) => m[1]);
}

/** Property names of a C# class, camel-cased to match `JsonKnownNamingPolicy.CamelCase`. */
function csClassFields(source, name, file) {
    const header = new RegExp(`class ${name}\\b[^{]*\\{`).exec(source);
    if (!header) {
        errors.push(`${file}: class ${name} not found`);
        return null;
    }
    const body = balancedBody(source, header.index + header[0].length - 1);
    if (body === null) {
        errors.push(`${file}: class ${name} has an unbalanced body`);
        return null;
    }
    return [...stripComments(body).matchAll(/public\s+[\w?.<>[\]]+\s+(\w+)\s*\{\s*get;/g)].map(
        (m) => m[1][0].toLowerCase() + m[1].slice(1),
    );
}

/** Text between the brace at `openIndex` and its match, comments and strings ignored. */
function balancedBody(source, openIndex) {
    let depth = 0;
    for (let i = openIndex; i < source.length; i++) {
        const char = source[i];
        if (char === "{") depth++;
        else if (char === "}") {
            depth--;
            if (depth === 0) return source.slice(openIndex + 1, i);
        }
    }
    return null;
}

function stripComments(text) {
    return text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/[^\n]*/g, "");
}

/**
 * The frozen pairings. `direction` documents who writes the payload; `csOptional` lists
 * fields the C# side deliberately does not carry (and why) so an intentional asymmetry is
 * recorded here rather than rediscovered during a debugging session.
 */
const pairings = [
    {
        label: "ProjectOptionsRequest",
        direction: "TS → C#",
        ts: { source: schemaTs, file: "src/ts/interop/schema.ts", name: "ProjectOptionsRequest" },
        cs: { source: interopCs, file: "src/CompilationInterop.cs", name: "ProjectOptionsDto" },
    },
    {
        label: "RunOptionsRequest",
        direction: "TS → C#",
        ts: { source: schemaTs, file: "src/ts/interop/schema.ts", name: "RunOptionsRequest" },
        cs: { source: interopCs, file: "src/CompilationInterop.cs", name: "RunOptionsDto" },
    },
    {
        label: "RunInteractiveOptionsRequest",
        direction: "TS → C#",
        ts: { source: schemaTs, file: "src/ts/interop/schema.ts", name: "RunInteractiveOptionsRequest" },
        cs: { source: interopCs, file: "src/CompilationInterop.cs", name: "RunInteractiveOptionsDto" },
    },
    {
        label: "RunAssemblyOptionsRequest",
        direction: "TS → C#",
        ts: { source: schemaTs, file: "src/ts/interop/schema.ts", name: "RunAssemblyOptionsRequest" },
        cs: { source: interopCs, file: "src/CompilationInterop.cs", name: "RunAssemblyOptionsDto" },
    },
    {
        label: "BuildResult",
        direction: "C# → TS",
        ts: { source: schemaTs, file: "src/ts/interop/schema.ts", name: "BuildResultWire" },
        cs: { source: interopCs, file: "src/CompilationInterop.cs", name: "BuildResultDto" },
    },
    {
        label: "RunResult",
        direction: "C# → TS",
        ts: { source: typesTs, file: "src/ts/types.ts", name: "RunResult" },
        cs: { source: runResultCs, file: "src/Services/RunResult.cs", name: "RunResult" },
    },
    {
        label: "Diagnostic",
        direction: "C# → TS",
        ts: { source: typesTs, file: "src/ts/types.ts", name: "Diagnostic" },
        cs: { source: diagnosticCs, file: "src/Services/Diagnostic.cs", name: "Diagnostic" },
    },
];

let comparedPairs = 0;
for (const pairing of pairings) {
    const tsFields = tsInterfaceFields(pairing.ts.source, pairing.ts.name, pairing.ts.file);
    const csFields = csClassFields(pairing.cs.source, pairing.cs.name, pairing.cs.file);
    if (!tsFields || !csFields) {
        continue;
    }
    comparedPairs++;

    const missingInCs = tsFields.filter((field) => !csFields.includes(field));
    const missingInTs = csFields.filter((field) => !tsFields.includes(field));
    assert(
        missingInCs.length === 0,
        `${pairing.label} (${pairing.direction}): ${pairing.ts.name} declares [${missingInCs.join(", ")}] ` +
            `which ${pairing.cs.name} does not carry`,
    );
    assert(
        missingInTs.length === 0,
        `${pairing.label} (${pairing.direction}): ${pairing.cs.name} declares [${missingInTs.join(", ")}] ` +
            `which ${pairing.ts.name} does not carry`,
    );
}

// ---------------------------------------------------------------------------
// 3. Frozen payloads cover the current version.
// ---------------------------------------------------------------------------

const fixtureDir = "test/fixtures/wire";
const { readdirSync } = await import("node:fs");
const fixtures = readdirSync(path.join(coreRoot, fixtureDir)).filter((name) => name.endsWith(".json"));
for (const shape of [
    "run-result-success",
    "build-result-success",
    "project-options-request",
    "run-options-request",
    "run-interactive-options-request",
    "run-assembly-options-request",
]) {
    assert(
        fixtures.includes(`${shape}.v${schemaVersion}.json`),
        `${fixtureDir}/${shape}.v${schemaVersion}.json is missing — every schema bump adds a frozen payload`,
    );
}

if (errors.length > 0) {
    console.error("Wire-contract validation failed:\n");
    for (const error of errors) {
        console.error(`- ${error}`);
    }
    process.exitCode = 1;
} else {
    console.log(
        `Wire-contract validation passed: SCHEMA_VERSION ${schemaVersion} is consistent across ` +
            `TypeScript and C#, ${comparedPairs} payload shapes match field-for-field, and ` +
            `${fixtures.length} frozen payloads are present.`,
    );
}
