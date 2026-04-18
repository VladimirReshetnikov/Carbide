// Fixture-driven parity test. Each directory under test/parity/ is one fixture:
//   Foo.csproj     — the project file.
//   *.cs           — source files discovered by the parser.
//   expected.json  — stable-field snapshot the parser must produce.
//
// Snapshotted fields are the stable ones: targetFrameworks, selectedTfm, properties (minus
// absolute paths), package/project-reference ids, source basenames, warning codes. Absolute
// paths and the full evaluationTrace vary by filesystem and aren't snapshotted.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseCsproj } from "../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PARITY_DIR = path.join(HERE, "parity");

function fixtures() {
    return readdirSync(PARITY_DIR).filter((name) => {
        const full = path.join(PARITY_DIR, name);
        return statSync(full).isDirectory();
    });
}

/** Strip the absolute parts of the parser's output so snapshots are path-stable. */
function snapshot(model) {
    const basenames = (paths) => paths.map((p) => path.basename(p)).sort();
    return {
        targetFrameworks: model.targetFrameworks,
        selectedTfm: model.evaluationTrace.targetFramework.selected,
        properties: { ...model.properties },
        packageReferences: model.packageReferences.map((r) => ({ id: r.id, version: r.version })),
        projectReferences: model.projectReferences,
        projectReferenceBasenames: basenames(model.projectReferences),
        sourceFileBasenames: basenames(model.sourceFiles),
        warningCodes: model.warnings.map((w) => w.code).sort(),
    };
}

function project(actual, expected) {
    // Project `actual` onto the fields `expected` cares about.
    const out = {};
    for (const key of Object.keys(expected)) {
        if (key === "properties") {
            const exp = expected.properties;
            const act = actual.properties;
            const picked = {};
            for (const propKey of Object.keys(exp)) {
                picked[propKey] = act[propKey];
            }
            out.properties = picked;
        } else if (key === "warningCodes") {
            out.warningCodes = [...actual.warningCodes].sort();
        } else {
            out[key] = actual[key];
        }
    }
    return out;
}

for (const name of fixtures()) {
    test(`parity fixture: ${name}`, async () => {
        const dir = path.join(PARITY_DIR, name);
        const expected = JSON.parse(readFileSync(path.join(dir, "expected.json"), "utf8"));
        const model = await parseCsproj(path.join(dir, "Foo.csproj"));
        const actual = snapshot(model);
        const expectedSorted = { ...expected, warningCodes: [...(expected.warningCodes ?? [])].sort() };
        assert.deepEqual(project(actual, expectedSorted), expectedSorted);
    });
}
