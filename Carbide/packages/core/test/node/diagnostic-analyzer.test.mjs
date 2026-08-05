// M12 — diagnostic analyzers. Registered via session.addAnalyzer exactly like a source
// generator; Carbide tells the two apart by what the assembly contains.
//
// The open question these answer is whether Roslyn's analyzer driver runs at all on
// single-threaded Mono-WASM. It does, but only with `concurrentAnalysis: false` — the
// concurrent path schedules through a thread pool this runtime does not have.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CarbideSession } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FIXTURE = path.resolve(HERE, "../fixtures/analyzer-dll");

function loadAnalyzerBytes() {
    const dll = path.join(FIXTURE, "CarbideTestAnalyzer.dll");
    try {
        return new Uint8Array(readFileSync(dll));
    } catch (e) {
        throw new Error(
            `CarbideTestAnalyzer.dll not found at ${dll}. Run \`npm run build:test-fixtures\` first. ` +
                `Underlying error: ${e.message}`,
        );
    }
}

// `lowerCased` trips CARBIDETEST001 (warning). Top-level statements must come first.
const WARNING_PROGRAM = `
Console.WriteLine(new lowerCased().ToString());

public class lowerCased { }
`.trim();

const CLEAN_PROGRAM = `
Console.WriteLine(new ProperlyNamed().ToString());

public class ProperlyNamed { }
`.trim();

// `Forbidden` trips CARBIDETEST002 (error).
const ERROR_PROGRAM = `
Console.WriteLine(new Forbidden().ToString());

public class Forbidden { }
`.trim();

test("an analyzer-only DLL is accepted and its warning reaches getDiagnostics", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    // Before analyzers ran, a DLL with no source generator was refused outright.
    const handle = session.addAnalyzer(loadAnalyzerBytes(), "CarbideTestAnalyzer");
    assert.equal(handle.kind, "analyzer");

    const project = session.createProject();
    project.addAnalyzer(handle);
    project.addSource("Program.cs", WARNING_PROGRAM);

    const diagnostics = await project.getDiagnostics();
    const rule = diagnostics.find((d) => d.id === "CARBIDETEST001");
    assert.ok(rule, `expected CARBIDETEST001, got ${JSON.stringify(diagnostics, null, 2)}`);
    assert.equal(rule.severity, "warning");
    assert.match(rule.message, /lowerCased/);
    // A warning must not stop the program running.
    assert.deepEqual(diagnostics.filter((d) => d.severity === "error"), []);
});

test("analyzer diagnostics are attributed to the source that triggered them", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addAnalyzer(session.addAnalyzer(loadAnalyzerBytes(), "CarbideTestAnalyzer"));
    project.addSource("Program.cs", WARNING_PROGRAM);

    const rule = (await project.getDiagnostics()).find((d) => d.id === "CARBIDETEST001");
    assert.ok(rule);
    // Location.None would be the tell-tale of a driver that ran but lost its context.
    assert.equal(rule.path, "Program.cs");
    assert.ok(rule.lineStart >= 0, `expected a real line, got ${rule.lineStart}`);
});

test("an analyzer error fails the build, like the compiler's own", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addAnalyzer(session.addAnalyzer(loadAnalyzerBytes(), "CarbideTestAnalyzer"));
    project.addSource("Program.cs", ERROR_PROGRAM);

    const built = await project.build();
    assert.equal(built.success, false, "an error-severity analyzer diagnostic must fail the build");
    assert.ok(
        built.diagnostics.some((d) => d.id === "CARBIDETEST002"),
        `expected CARBIDETEST002 among ${JSON.stringify(built.diagnostics)}`,
    );
});

test("a clean program produces no analyzer diagnostics and still runs", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addAnalyzer(session.addAnalyzer(loadAnalyzerBytes(), "CarbideTestAnalyzer"));
    project.addSource("Program.cs", CLEAN_PROGRAM);

    const diagnostics = await project.getDiagnostics();
    assert.deepEqual(diagnostics.filter((d) => d.id.startsWith("CARBIDETEST")), []);

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
});

test("without the analyzer attached, the same sources report nothing", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    // The negative half: otherwise the assertions above would not show the analyzer ran.
    const project = session.createProject();
    project.addSource("Program.cs", WARNING_PROGRAM);

    const diagnostics = await project.getDiagnostics();
    assert.deepEqual(diagnostics.filter((d) => d.id.startsWith("CARBIDETEST")), []);
});

test("an analyzer attached to one project does not leak into a sibling", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const handle = session.addAnalyzer(loadAnalyzerBytes(), "CarbideTestAnalyzer");

    const analysed = session.createProject();
    analysed.addAnalyzer(handle);
    analysed.addSource("Program.cs", WARNING_PROGRAM);

    const plain = session.createProject();
    plain.addSource("Program.cs", WARNING_PROGRAM);

    assert.ok((await analysed.getDiagnostics()).some((d) => d.id === "CARBIDETEST001"));
    assert.deepEqual(
        (await plain.getDiagnostics()).filter((d) => d.id.startsWith("CARBIDETEST")),
        [],
    );
});

test("a DLL with neither a generator nor an analyzer is still refused", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const helper = new Uint8Array(
        readFileSync(path.resolve(HERE, "../fixtures/helper-dll/MyHelper.dll")),
    );
    assert.throws(
        () => session.addAnalyzer(helper, "MyHelper"),
        /no usable source generator or diagnostic analyzer/i,
    );
});
