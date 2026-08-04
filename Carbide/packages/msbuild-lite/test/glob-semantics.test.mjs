// Compile-item glob semantics. A pattern that matches nothing does not fail — the project
// simply compiles without those sources, and the error surfaces as CS0246 against code that
// is not at fault. These tests pin the wildcard rules and the walk root.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { expandGlob, globToRegex } from "../dist/compile-items.js";

/** Build a fixture tree: <root>/proj/{File1.cs,FileA.cs,Sub/Deep.cs} and <root>/Shared/Util.cs */
function scaffold(t) {
    const root = mkdtempSync(path.join(tmpdir(), "carbide-glob-"));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    mkdirSync(path.join(root, "proj", "Sub"), { recursive: true });
    mkdirSync(path.join(root, "Shared"), { recursive: true });
    writeFileSync(path.join(root, "proj", "File1.cs"), "class A{}");
    writeFileSync(path.join(root, "proj", "FileA.cs"), "class B{}");
    writeFileSync(path.join(root, "proj", "Sub", "Deep.cs"), "class C{}");
    writeFileSync(path.join(root, "Shared", "Util.cs"), "class D{}");
    return { root, proj: path.join(root, "proj") };
}

const names = (matches) => matches.map((p) => path.basename(p)).sort();

test("`*` matches within one segment", async (t) => {
    const { proj } = scaffold(t);
    assert.deepEqual(names(await expandGlob(proj, "*.cs")), ["File1.cs", "FileA.cs"]);
});

test("`**` crosses directory levels, including zero", async (t) => {
    const { proj } = scaffold(t);
    assert.deepEqual(names(await expandGlob(proj, "**/*.cs")), ["Deep.cs", "File1.cs", "FileA.cs"]);
    assert.deepEqual(names(await expandGlob(proj, "Sub/**/*.cs")), ["Deep.cs"]);
});

test("`?` matches exactly one character", async (t) => {
    const { proj } = scaffold(t);
    // Previously `?` was escaped to a literal question mark, which cannot appear in a Windows
    // filename — so this pattern silently contributed nothing.
    assert.deepEqual(names(await expandGlob(proj, "File?.cs")), ["File1.cs", "FileA.cs"]);
    assert.equal(globToRegex("File?.cs").source, "^File[^/]\\.cs$");
    // One character, not several.
    assert.deepEqual(names(await expandGlob(proj, "File??.cs")), []);
});

test("a pattern may reach outside the project directory", async (t) => {
    const { proj } = scaffold(t);
    // `..\Shared\*.cs` is the standard shared-source idiom and used to match nothing, because
    // the walk was rooted at the project directory regardless of the pattern.
    assert.deepEqual(names(await expandGlob(proj, "../Shared/*.cs")), ["Util.cs"]);
    assert.deepEqual(names(await expandGlob(proj, "..\\Shared\\*.cs")), ["Util.cs"], "backslashes too");
});

test("a wildcard-free pattern resolves to the single file", async (t) => {
    const { proj } = scaffold(t);
    assert.deepEqual(names(await expandGlob(proj, "Sub/Deep.cs")), ["Deep.cs"]);
    assert.deepEqual(names(await expandGlob(proj, "../Shared/Util.cs")), ["Util.cs"]);
    assert.deepEqual(names(await expandGlob(proj, "Nope.cs")), []);
});

test("only .cs files are ever returned", async (t) => {
    const { proj } = scaffold(t);
    writeFileSync(path.join(proj, "notes.txt"), "hi");
    assert.deepEqual(names(await expandGlob(proj, "*")), ["File1.cs", "FileA.cs"]);
});

test("matching is case-sensitive on every host", async (t) => {
    const { proj } = scaffold(t);
    // Deliberate: MSBuild inherits the filesystem's behaviour and so answers differently on
    // Windows and Linux. Carbide gives one answer everywhere.
    assert.deepEqual(names(await expandGlob(proj, "file1.cs")), []);
    assert.deepEqual(names(await expandGlob(proj, "sub/*.cs")), []);
});

test("regex metacharacters in a pattern stay literal", async (t) => {
    const { proj } = scaffold(t);
    writeFileSync(path.join(proj, "A+B.cs"), "class E{}");
    assert.deepEqual(names(await expandGlob(proj, "A+B.cs")), ["A+B.cs"]);
    // `+` must not be read as a repetition operator.
    assert.deepEqual(names(await expandGlob(proj, "AB.cs")), []);
});
