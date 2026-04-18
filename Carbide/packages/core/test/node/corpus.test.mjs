// M2.5 golden-corpus seed. Each directory under test/node/corpus/ is one Shape-S2 fixture:
//   *.cs            — source files to feed into Project.addSource (basename as path)
//   expected.json   — { stdOut: string, exitCode?: number }
// The corpus grows incrementally toward vision §9's "≥ 50 programs" goal; M2 seeds five.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CarbideSession } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CORPUS_DIR = path.join(HERE, "corpus");

function fixtureNames() {
    return readdirSync(CORPUS_DIR).filter((name) => {
        const full = path.join(CORPUS_DIR, name);
        return statSync(full).isDirectory();
    });
}

function loadFixture(name) {
    const dir = path.join(CORPUS_DIR, name);
    const files = readdirSync(dir).filter((f) => f.endsWith(".cs"));
    const expected = JSON.parse(readFileSync(path.join(dir, "expected.json"), "utf8"));
    const sources = files.map((f) => ({
        path: f,
        code: readFileSync(path.join(dir, f), "utf8"),
    }));
    return { name, sources, expected };
}

// One shared session across the whole corpus. Each fixture gets its own Project.
let sharedSession;
test.before(async () => {
    sharedSession = await CarbideSession.initializeAsync();
});
test.after(async () => {
    if (sharedSession) await sharedSession.shutdown();
});

for (const name of fixtureNames()) {
    test(`corpus fixture: ${name}`, async () => {
        const { sources, expected } = loadFixture(name);
        const project = sharedSession.createProject();
        for (const src of sources) {
            project.addSource(src.path, src.code);
        }

        const diagnostics = await project.getDiagnostics();
        const errors = diagnostics.filter((d) => d.severity === "error");
        assert.deepEqual(
            errors,
            [],
            `fixture '${name}' produced errors: ${JSON.stringify(errors, null, 2)}`,
        );

        const result = await project.run();
        assert.equal(
            result.success,
            true,
            `fixture '${name}' run failed: ${JSON.stringify(result, null, 2)}`,
        );
        assert.equal(result.stdOut, expected.stdOut, `fixture '${name}' stdOut mismatch`);
        if (typeof expected.exitCode === "number") {
            assert.equal(result.exitCode, expected.exitCode, `fixture '${name}' exitCode mismatch`);
        }
    });
}
