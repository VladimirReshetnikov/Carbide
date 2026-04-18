// Fast CLI smoke: help / version do not boot the WASM runtime.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CLI = path.resolve(HERE, "..", "dist", "bin", "carbide.js");

function run(args) {
    const result = spawnSync(process.execPath, [CLI, ...args], {
        encoding: "utf8",
        shell: false,
    });
    return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("--help prints usage and exits 0", () => {
    const { status, stdout } = run(["--help"]);
    assert.equal(status, 0);
    assert.match(stdout, /Usage: carbide/);
    assert.match(stdout, /build/);
    assert.match(stdout, /run/);
    assert.match(stdout, /validate/);
});

test("--version prints and exits 0", () => {
    const { status, stdout } = run(["--version"]);
    assert.equal(status, 0);
    assert.match(stdout, /^\d+\.\d+\.\d+/);
});

test("unknown command exits 3", () => {
    const { status, stderr } = run(["nosuch"]);
    assert.equal(status, 3);
    assert.match(stderr, /unknown command/);
});

test("build --help is per-command", () => {
    const { status, stdout } = run(["build", "--help"]);
    assert.equal(status, 0);
    assert.match(stdout, /Usage: carbide build/);
});
