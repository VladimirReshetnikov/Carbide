// TFM fallback ordering. This decides which `lib/<tfm>/` folder is taken from a package, so
// a chain that is too short does not fail loudly — the package resolves and simply supplies
// no references, and the build blames the user's own source with CS0246.

import { test } from "node:test";
import assert from "node:assert/strict";
import { parseTfm, compatibleLibFolders, pickBestLibFolder } from "../../dist/tfm-compat.js";

const net10 = parseTfm("net10.0");
const pick = (folders, target = net10) => pickBestLibFolder(target, folders);

test("the net10.0 chain reaches every framework it is genuinely compatible with", () => {
    const chain = compatibleLibFolders(net10);
    // The unified line, newest first.
    for (const label of ["net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0"]) {
        assert.ok(chain.includes(label), `${label} missing from the net10.0 chain`);
    }
    // .NET 5+ is the continuation of .NET Core, so netcoreapp assets are consumable.
    assert.ok(chain.includes("netcoreapp3.1"));
    assert.ok(chain.includes("netcoreapp1.0"));
    // netstandard all the way down — plenty of libraries still ship only 1.x.
    assert.ok(chain.includes("netstandard2.1"));
    assert.ok(chain.includes("netstandard1.0"));
});

test("the chain is ordered best-match first", () => {
    const chain = compatibleLibFolders(net10);
    const rank = (label) => chain.indexOf(label);
    assert.ok(rank("net10.0") < rank("net5.0"), "newer net before older net");
    assert.ok(rank("net5.0") < rank("netcoreapp3.1"), "net before netcoreapp");
    assert.ok(rank("netcoreapp3.1") < rank("netcoreapp1.0"), "newer netcoreapp first");
    assert.ok(rank("netcoreapp1.0") < rank("netstandard2.1"), "netcoreapp before netstandard");
    assert.ok(rank("netstandard2.1") < rank("netstandard1.0"), "newer netstandard first");
});

test("a package shipping only an older-but-compatible folder is usable", () => {
    // Each of these used to resolve to null, so the package contributed nothing.
    assert.equal(pick(["net5.0"]), "net5.0");
    assert.equal(pick(["netcoreapp3.1"]), "netcoreapp3.1");
    assert.equal(pick(["netstandard1.3"]), "netstandard1.3");
    assert.equal(pick(["netstandard1.0"]), "netstandard1.0");
});

test("the closest compatible folder wins when several are present", () => {
    assert.equal(pick(["netstandard2.0", "net8.0"]), "net8.0");
    assert.equal(pick(["netstandard1.3", "netstandard2.0"]), "netstandard2.0");
    assert.equal(pick(["netcoreapp3.1", "net6.0", "netstandard2.0"]), "net6.0");
    assert.equal(pick(["net10.0", "net9.0"]), "net10.0", "an exact match is preferred");
});

test(".NET Framework assets stay incompatible", () => {
    // net5.0+ genuinely cannot consume these; NuGet's old fallback is deprecated.
    assert.equal(pick(["net472"]), null);
    assert.equal(pick(["net48", "net462"]), null);
});

test("folder matching is case-insensitive but returns the original spelling", () => {
    assert.equal(pick(["NetStandard2.0"]), "NetStandard2.0");
    assert.equal(pick(["NET8.0"]), "NET8.0");
});

test("a netstandard target consumes its own version and every earlier one", () => {
    const chain = compatibleLibFolders(parseTfm("netstandard2.0"));
    assert.equal(chain[0], "netstandard2.0", "its own version first");
    assert.ok(!chain.includes("netstandard2.1"), "never a later version");
    assert.ok(chain.includes("netstandard1.0"), "down to 1.0");
    // The old loop bottomed out at 2.0, so a 1.x target produced an empty chain.
    assert.ok(compatibleLibFolders(parseTfm("netstandard1.3")).includes("netstandard1.0"));
    assert.ok(!compatibleLibFolders(parseTfm("netstandard1.3")).includes("netstandard1.4"));
});

test("unsupported target monikers are refused at parse time", () => {
    assert.equal(parseTfm("netcoreapp3.1"), null);
    assert.equal(parseTfm("net472"), null);
    assert.equal(parseTfm("net10.0;net8.0"), null, "multi-TFM strings are not a target");
});
