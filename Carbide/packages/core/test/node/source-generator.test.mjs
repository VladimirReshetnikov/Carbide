// M12 acceptance: a Roslyn source generator registered via session.addAnalyzer and attached
// to a project with project.addAnalyzer contributes source that user code compiles against,
// and that source survives into the emitted assembly and runs.
//
// The fixture generator (test/fixtures/generator-dll) is a real incremental generator: it
// contributes the `[GenerateToString]` attribute through post-initialization output, then
// uses a syntax provider and the semantic model to emit a `ToString` override into each
// annotated partial class.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CarbideSession } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const FIXTURE = path.resolve(HERE, "../fixtures/generator-dll");

function loadGeneratorBytes() {
    const dll = path.join(FIXTURE, "CarbideTestGenerator.dll");
    try {
        return new Uint8Array(readFileSync(dll));
    } catch (e) {
        throw new Error(
            `CarbideTestGenerator.dll not found at ${dll}. Run \`npm run build:test-fixtures\` first. ` +
                `Underlying error: ${e.message}`,
        );
    }
}

// Top-level statements must precede type declarations, hence the ordering.
const PROGRAM = `
Console.WriteLine(new Point { X = 1, Y = 2 });

[CarbideTest.GenerateToString]
public partial class Point
{
    public int X { get; set; }
    public int Y { get; set; }
}
`.trim();

test("a source generator's output compiles, emits, and runs", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const handle = session.addAnalyzer(loadGeneratorBytes(), "CarbideTestGenerator");
    assert.equal(typeof handle.id, "string");
    assert.equal(handle.id.length, 32, "handle id should be a 32-char GUID hex");
    assert.equal(handle.name, "CarbideTestGenerator");
    assert.equal(handle.kind, "analyzer");
    assert.equal(handle.disposed, false);

    const project = session.createProject();
    project.addAnalyzer(handle);
    project.addSource("Program.cs", PROGRAM);

    // The attribute exists only because of post-initialization output, and ToString exists
    // only because of the syntax-provider pass — so a clean diagnostics run already proves
    // both halves of the generator reached the compilation.
    const diagnostics = await project.getDiagnostics();
    assert.deepEqual(
        diagnostics.filter((d) => d.severity === "error"),
        [],
        `expected no errors, got: ${JSON.stringify(diagnostics, null, 2)}`,
    );

    const result = await project.run();
    assert.equal(result.success, true, JSON.stringify(result));
    assert.equal(result.stdOut.trim(), "Point { X = 1, Y = 2 }");
});

test("generated source reaches the emitted assembly, not just the compilation", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addAnalyzer(session.addAnalyzer(loadGeneratorBytes(), "CarbideTestGenerator"));
    project.addSource("Program.cs", PROGRAM);

    const built = await project.build();
    assert.equal(built.success, true, JSON.stringify(built.diagnostics));
    assert.ok(built.pe instanceof Uint8Array && built.pe.length > 0);

    // Run the emitted bytes in a fresh execution path. `build` and `run` compile
    // independently, so this is what shows the generator ran on the emit path too rather
    // than only on the path `run` happens to take.
    const ran = await session.runAssembly({ pe: built.pe });
    assert.equal(ran.success, true, JSON.stringify(ran));
    assert.equal(ran.stdOut.trim(), "Point { X = 1, Y = 2 }");
});

test("without the generator attached, the same sources fail to compile", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    // The negative half: if this compiled, the test above would prove nothing about the
    // generator having run.
    const project = session.createProject();
    project.addSource("Program.cs", PROGRAM);

    const diagnostics = await project.getDiagnostics();
    const errors = diagnostics.filter((d) => d.severity === "error");
    assert.ok(errors.length > 0, "expected the missing attribute to be an error");
    // The attribute's namespace is what the compiler names first: with no generator to
    // contribute it, `CarbideTest` does not exist at all.
    assert.ok(
        errors.some((d) => d.message.includes("CarbideTest")),
        `expected an error naming the ungenerated attribute's namespace, got: ${JSON.stringify(errors, null, 2)}`,
    );
});

test("a generator attached to one project does not leak into a sibling project", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const handle = session.addAnalyzer(loadGeneratorBytes(), "CarbideTestGenerator");

    const withGenerator = session.createProject();
    withGenerator.addAnalyzer(handle);
    withGenerator.addSource("Program.cs", PROGRAM);

    const without = session.createProject();
    without.addSource("Program.cs", PROGRAM);

    assert.deepEqual(
        (await withGenerator.getDiagnostics()).filter((d) => d.severity === "error"),
        [],
    );
    assert.ok(
        (await without.getDiagnostics()).filter((d) => d.severity === "error").length > 0,
        "registering a generator on the session must not attach it to every project",
    );
});

test("the generator assembly's own types stay invisible to user code", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const project = session.createProject();
    project.addAnalyzer(session.addAnalyzer(loadGeneratorBytes(), "CarbideTestGenerator"));
    // A generator is a compile-time tool, not a reference. Naming one of its types has to
    // fail, or attaching a generator would silently widen the program's API surface.
    project.addSource(
        "Program.cs",
        "Console.WriteLine(typeof(CarbideTestGenerator.GenerateToStringGenerator).Name);",
    );

    const errors = (await project.getDiagnostics()).filter((d) => d.severity === "error");
    assert.ok(
        errors.length > 0,
        "the generator assembly must not become a metadata reference",
    );
});

test("a DLL with no source generator is refused at registration", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const helper = new Uint8Array(
        readFileSync(path.resolve(HERE, "../fixtures/helper-dll/MyHelper.dll")),
    );
    // Accepting this and contributing nothing is the failure mode worth guarding: the user
    // would see errors about source that was never generated, with no hint that the assembly
    // they registered was the wrong one.
    assert.throws(
        () => session.addAnalyzer(helper, "MyHelper"),
        /no usable source generator/i,
    );
});

test("removeAnalyzer detaches the generator from projects that had it", async (t) => {
    const session = await CarbideSession.initializeAsync();
    t.after(async () => await session.shutdown());

    const handle = session.addAnalyzer(loadGeneratorBytes(), "CarbideTestGenerator");
    const project = session.createProject();
    project.addAnalyzer(handle);
    project.addSource("Program.cs", PROGRAM);

    assert.deepEqual((await project.getDiagnostics()).filter((d) => d.severity === "error"), []);

    session.removeAnalyzer(handle);
    assert.equal(handle.disposed, true);
    assert.ok(
        (await project.getDiagnostics()).filter((d) => d.severity === "error").length > 0,
        "removing the generator must stop its source from being contributed",
    );
    assert.throws(() => project.addAnalyzer(handle), /disposed/);
});
