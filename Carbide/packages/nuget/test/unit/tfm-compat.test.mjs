import { test } from "node:test";
import assert from "node:assert/strict";
import {
    parseTfm,
    compatibleLibFolders,
    pickBestLibFolder,
    collectLibFolders,
    libFolderOf,
} from "../../dist/tfm-compat.js";

test("parseTfm: net10.0 / net8.0 / netstandard2.0", () => {
    assert.equal(parseTfm("net10.0")?.family, "net");
    assert.equal(parseTfm("net10.0")?.version, 100);
    assert.equal(parseTfm("net8.0")?.version, 80);
    assert.equal(parseTfm("netstandard2.0")?.family, "netstandard");
    assert.equal(parseTfm("netstandard2.0")?.version, 20);
});

test("parseTfm: rejects netcoreapp and net-framework moniker shapes", () => {
    assert.equal(parseTfm("netcoreapp3.1"), null);
    assert.equal(parseTfm("net472"), null);
});

test("compatibleLibFolders: net10.0 ladder", () => {
    const tfm = parseTfm("net10.0");
    const folders = compatibleLibFolders(tfm);
    assert.ok(folders.includes("net10.0"));
    assert.ok(folders.includes("net8.0"));
    assert.ok(folders.includes("netstandard2.1"));
    assert.ok(folders.includes("netstandard2.0"));
    // net10.0 should win over earlier majors.
    const indexNet10 = folders.indexOf("net10.0");
    const indexNet6 = folders.indexOf("net6.0");
    assert.ok(indexNet10 < indexNet6, "net10.0 should precede net6.0 in the compat ladder");
});

test("pickBestLibFolder: prefers net10.0 when available", () => {
    const tfm = parseTfm("net10.0");
    assert.equal(pickBestLibFolder(tfm, ["net6.0", "net10.0", "netstandard2.0"]), "net10.0");
    assert.equal(pickBestLibFolder(tfm, ["net6.0", "netstandard2.0"]), "net6.0");
    assert.equal(pickBestLibFolder(tfm, ["netstandard2.1", "netstandard2.0"]), "netstandard2.1");
    assert.equal(pickBestLibFolder(tfm, ["netstandard2.0"]), "netstandard2.0");
    assert.equal(pickBestLibFolder(tfm, ["netcoreapp3.1"]), null);
});

test("libFolderOf / collectLibFolders", () => {
    assert.equal(libFolderOf("lib/net10.0/Foo.dll"), "net10.0");
    assert.equal(libFolderOf("lib/netstandard2.0/sub/Foo.dll"), "netstandard2.0");
    assert.equal(libFolderOf("tools/Foo.dll"), null);
    const folders = collectLibFolders([
        "lib/net10.0/A.dll",
        "lib/net8.0/B.dll",
        "lib/net10.0/C.dll",
        "build/foo.targets",
    ]);
    assert.deepEqual([...folders].sort(), ["net10.0", "net8.0"]);
});
