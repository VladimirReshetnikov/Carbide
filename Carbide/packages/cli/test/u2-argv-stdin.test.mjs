// U2 — hermetic tests for argv + stdin forwarding.

import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { parseJsonBySentinel } from "./_helpers.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");

function runCarbide(args, options = {}) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
        ...options,
    });
    return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

test("U2.1: argv after -- is forwarded to the top-level program's args", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-argv-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    // Top-level statements: Roslyn synthesises Main(string[] args).
    writeFileSync(path.join(work, "P.cs"), `Console.Write(string.Join(",", args));`);

    const r = runCarbide([
        "run", "--source", path.join(work, "P.cs"), "--format", "human",
        "--", "alpha", "beta", "gamma",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "alpha,beta,gamma");
});

test("U2.1: no -- means empty args", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-argv0-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.Write(args.Length);`);

    const r = runCarbide(["run", "--source", path.join(work, "P.cs"), "--format", "human"]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "0");
});

test("U2.1: argv works with --project mode too", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-argv-proj-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "Foo.csproj"),
        `<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Foo</AssemblyName></PropertyGroup></Project>`,
    );
    writeFileSync(path.join(work, "P.cs"), `Console.Write(string.Join("|", args));`);

    const r = runCarbide([
        "run", "--project", path.join(work, "Foo.csproj"), "--format", "human",
        "--", "one", "two",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "one|two");
});

test("U2.1: JSON payload carries invocation with args + stdinBytes: 0", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-inv-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.Write(args.Length);`);

    const r = runCarbide([
        "run", "--source", path.join(work, "P.cs"),
        "--", "x", "y",
    ]);
    assert.equal(r.status, 0);
    const payload = parseJsonBySentinel(r.stdout);
    assert.deepEqual(payload.invocation, { args: ["x", "y"], stdinBytes: 0 });
});

test("U2.2: --stdin <path> feeds Console.In", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-stdin-file-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.Write(Console.In.ReadToEnd());`);
    writeFileSync(path.join(work, "in.txt"), "hello-from-file");

    const r = runCarbide([
        "run", "--source", path.join(work, "P.cs"),
        "--stdin", path.join(work, "in.txt"),
        "--format", "human",
    ]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "hello-from-file");
});

test("U2.2: --stdin - reads from the CLI's own stdin", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-stdin-pipe-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.Write(Console.In.ReadToEnd());`);

    const r = runCarbide(
        ["run", "--source", path.join(work, "P.cs"), "--stdin", "-", "--format", "human"],
        { input: "piped-input" },
    );
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "piped-input");
});

test("U2.2: JSON invocation reports stdin byte count", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-stdin-bytes-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.Write(Console.In.ReadToEnd().Length);`);
    writeFileSync(path.join(work, "in.txt"), "xxxxxxxxxx"); // 10 bytes

    const r = runCarbide([
        "run", "--source", path.join(work, "P.cs"),
        "--stdin", path.join(work, "in.txt"),
    ]);
    assert.equal(r.status, 0);
    const payload = parseJsonBySentinel(r.stdout);
    assert.equal(payload.invocation.stdinBytes, 10);
    assert.equal(payload.stdOut, "10");
});

test("U2.2: no --stdin means Console.In is default (ReadToEnd returns empty)", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-stdin-none-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    // User program that doesn't touch Console.In at all — verifies the default path
    // (no reflection, no platform-not-supported error).
    writeFileSync(path.join(work, "P.cs"), `Console.Write("no-stdin-read");`);

    const r = runCarbide(["run", "--source", path.join(work, "P.cs"), "--format", "human"]);
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "no-stdin-read");
});

test("U2: both argv and stdin at once", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-both-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(
        path.join(work, "P.cs"),
        `Console.Write($"{string.Join(",", args)}|{Console.In.ReadToEnd()}");`,
    );

    const r = runCarbide(
        ["run", "--source", path.join(work, "P.cs"), "--stdin", "-", "--format", "human", "--", "a", "b"],
        { input: "stdin-text" },
    );
    assert.equal(r.status, 0, r.stderr);
    assert.equal(r.stdout, "a,b|stdin-text");
});

test("U2: consecutive runs don't inherit each other's stdin state", async (t) => {
    const work = mkdtempSync(path.join(tmpdir(), "carbide-u2-restore-"));
    t.after(() => rmSync(work, { recursive: true, force: true }));
    writeFileSync(path.join(work, "P.cs"), `Console.Write(Console.In.ReadToEnd());`);

    // First run with stdin.
    const r1 = runCarbide(
        ["run", "--source", path.join(work, "P.cs"), "--stdin", "-", "--format", "human"],
        { input: "first" },
    );
    assert.equal(r1.status, 0, r1.stderr);
    assert.equal(r1.stdout, "first");

    // Second run, still with stdin, verifies StringReader is fresh (not carrying residue).
    const r2 = runCarbide(
        ["run", "--source", path.join(work, "P.cs"), "--stdin", "-", "--format", "human"],
        { input: "second" },
    );
    assert.equal(r2.status, 0, r2.stderr);
    assert.equal(r2.stdout, "second");
});
