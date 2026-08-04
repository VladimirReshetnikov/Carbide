// M8 — Webcil images must work as compile-time metadata.
//
// With `<WasmEnableWebcil>true</WasmEnableWebcil>` the publish pipeline emits no `.dll` at
// all: every managed assembly in `_framework/` is a `.wasm` file wrapping a Webcil
// container. Webcil is not a PE, so `PEReader` rejects it — before M8 this silently loaded
// zero references and every user compilation failed with CS0518.
//
// The ref-pack path (the normal one) ships real PEs and is unaffected, so these tests force
// the fallback by handing the session an adapter that reports no ref pack.

import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { CarbideSession } from "../../dist/index.js";
import { NodeHostAdapter } from "../../dist/node.js";

const frameworkDir = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    "../../src/bin/Release/net10.0/publish/wwwroot/_framework",
);

/** Forces the M1/M2 runtime-assembly fallback instead of the ref pack. */
class NoRefPackAdapter extends NodeHostAdapter {
    resolveReferencePack() {
        return Promise.resolve(null);
    }
}

test("the publish output ships Webcil-wrapped assemblies", () => {
    assert.ok(existsSync(frameworkDir), `${frameworkDir} is missing — run the Mono-WASM publish first.`);
    const assets = readdirSync(frameworkDir);

    // A representative BCL assembly the runtime always carries.
    const coreLib = assets.find((name) => name === "System.Private.CoreLib.wasm");
    assert.ok(coreLib, "expected System.Private.CoreLib.wasm in the publish output");

    const bytes = readFileSync(path.join(frameworkDir, coreLib));
    assert.equal(
        bytes.subarray(0, 4).toString("latin1"),
        "\0asm",
        "expected a WebAssembly wrapper around the Webcil payload",
    );
    // The Webcil payload lives in a data segment; the magic must appear somewhere in it.
    assert.notEqual(bytes.indexOf(Buffer.from("WbIL", "latin1")), -1, "no WbIL payload found");
});

test("compilation succeeds against Webcil runtime assemblies (no ref pack)", async (t) => {
    const session = await CarbideSession.initializeAsync({ hostAdapter: new NoRefPackAdapter() });
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", 'Console.WriteLine("webcil metadata works");');

    const result = await project.run();
    assert.equal(
        result.success,
        true,
        `expected success; diagnostics: ${JSON.stringify(result.diagnostics.filter((d) => d.severity === "error"))}`,
    );
    assert.equal(result.stdOut, "webcil metadata works\n");
});

test("Webcil metadata carries real type information, not just an empty shell", async (t) => {
    const session = await CarbideSession.initializeAsync({ hostAdapter: new NoRefPackAdapter() });
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    // Touches several assemblies' metadata: collections, LINQ, string formatting, and the
    // core numeric types. A partially-decoded metadata root would fail to bind one of them.
    project.addSource(
        "Program.cs",
        [
            "using System.Collections.Generic;",
            "using System.Linq;",
            "var squares = Enumerable.Range(1, 5).Select(n => n * n).ToList();",
            "var lookup = new Dictionary<string, int> { [\"total\"] = squares.Sum() };",
            'Console.WriteLine($"{string.Join(",", squares)}|{lookup["total"]:D3}");',
        ].join("\n"),
    );

    const result = await project.run();
    assert.equal(
        result.success,
        true,
        `expected success; diagnostics: ${JSON.stringify(result.diagnostics.filter((d) => d.severity === "error"))}`,
    );
    assert.equal(result.stdOut, "1,4,9,16,25|055\n");
});

test("a genuine compile error still reports normally on the Webcil path", async (t) => {
    const session = await CarbideSession.initializeAsync({ hostAdapter: new NoRefPackAdapter() });
    t.after(async () => {
        await session.shutdown();
    });

    const project = session.createProject();
    project.addSource("Program.cs", "Console.WriteLine(NoSuchSymbol);");

    const result = await project.run();
    assert.equal(result.success, false);
    // CS0103 (not CS0518) proves the reference set loaded: a missing metadata set would
    // fail with "predefined type not defined" long before name resolution ran.
    assert.ok(
        result.diagnostics.some((d) => d.id === "CS0103"),
        `expected CS0103, got ${JSON.stringify(result.diagnostics.map((d) => d.id))}`,
    );
    assert.ok(
        !result.diagnostics.some((d) => d.id === "CS0518"),
        "CS0518 means the metadata references did not load at all",
    );
});
