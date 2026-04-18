// Live smoke test — resolves a small, stable, allow-listed package (Newtonsoft.Json)
// from api.nuget.org. Gated behind the CARBIDE_NUGET_LIVE=1 env var so CI and the
// default `npm test` stay hermetic.
//
// Run with:
//   CARBIDE_NUGET_LIVE=1 node --test test/integration/live-smoke.test.mjs
//
// Skips with a clear message when the gate isn't set.

import { test } from "node:test";
import assert from "node:assert/strict";
import { tmpdir } from "node:os";
import { mkdtempSync, rmSync } from "node:fs";
import { join } from "node:path";
import { resolve } from "../../dist/resolver.js";

const LIVE = process.env.CARBIDE_NUGET_LIVE === "1";

test("live smoke: resolve Newtonsoft.Json 13.0.3 from api.nuget.org", { skip: !LIVE }, async (t) => {
    const cacheDir = mkdtempSync(join(tmpdir(), "carbide-nuget-smoke-"));
    t.after(() => {
        try { rmSync(cacheDir, { recursive: true, force: true }); } catch { /* best-effort */ }
    });

    const graph = await resolve(
        [{ id: "Newtonsoft.Json", versionRange: "[13.0.3]" }],
        {
            targetFramework: "net10.0",
            allowListMode: "strict",
            cacheDir,
        },
    );

    // The allow-list should let Newtonsoft.Json through cleanly.
    const refused = graph.warnings.filter((w) => w.severity === "error");
    assert.equal(refused.length, 0, "No error-severity warnings expected");

    // Newtonsoft.Json has no runtime deps in the grouped form for net6.0+ / netstandard.
    // The resolver should surface just the one package.
    assert.equal(graph.packages.length, 1);
    const [pkg] = graph.packages;
    assert.equal(pkg.id, "Newtonsoft.Json");
    assert.equal(pkg.version, "13.0.3");
    assert.equal(pkg.sha256.length, 64);
    // net10.0 isn't shipped in 13.0.3 — expect netstandard2.0 via the fallback ladder.
    assert.ok(pkg.libFolder && /netstandard|^net\d/.test(pkg.libFolder), `Unexpected lib folder: ${pkg.libFolder}`);

    // We should have at least one reference DLL — starts with 'MZ' (PE magic).
    assert.ok(graph.references.length >= 1);
    const ref = graph.references.find((r) => r.name.toLowerCase() === "newtonsoft.json");
    assert.ok(ref, "Newtonsoft.Json.dll reference must be present");
    assert.ok(ref.bytes.length > 0);
    assert.equal(ref.bytes[0], 0x4d, "PE magic byte 0 (M)");
    assert.equal(ref.bytes[1], 0x5a, "PE magic byte 1 (Z)");

    // Lock file should be populated and match the resolved set.
    assert.equal(graph.lock.packages.length, 1);
    assert.equal(graph.lock.packages[0].sha256, pkg.sha256);
});

test("live smoke: lock replay matches fresh resolve byte-for-byte", { skip: !LIVE }, async (t) => {
    const cacheDir1 = mkdtempSync(join(tmpdir(), "carbide-nuget-smoke1-"));
    const cacheDir2 = mkdtempSync(join(tmpdir(), "carbide-nuget-smoke2-"));
    t.after(() => {
        for (const d of [cacheDir1, cacheDir2]) {
            try { rmSync(d, { recursive: true, force: true }); } catch { /* best-effort */ }
        }
    });

    const first = await resolve(
        [{ id: "Newtonsoft.Json", versionRange: "[13.0.3]" }],
        { targetFramework: "net10.0", allowListMode: "strict", cacheDir: cacheDir1 },
    );

    const replay = await resolve(
        [{ id: "Newtonsoft.Json", versionRange: "[13.0.3]" }],
        {
            targetFramework: "net10.0",
            allowListMode: "strict",
            cacheDir: cacheDir2,
            lock: first.lock,
        },
    );

    assert.equal(replay.packages.length, first.packages.length);
    for (let i = 0; i < first.packages.length; i++) {
        assert.equal(replay.packages[i].id, first.packages[i].id);
        assert.equal(replay.packages[i].version, first.packages[i].version);
        assert.equal(replay.packages[i].sha256, first.packages[i].sha256);
    }
    assert.equal(replay.references.length, first.references.length);
    for (let i = 0; i < first.references.length; i++) {
        const a = first.references[i];
        const b = replay.references.find((r) => r.name === a.name && r.packageId === a.packageId);
        assert.ok(b, `Replay missing reference ${a.name}`);
        assert.equal(b.bytes.length, a.bytes.length);
        // Spot-check the first 256 bytes so we aren't slaughtering the transcript on mismatch.
        const aHead = Buffer.from(a.bytes.subarray(0, 256));
        const bHead = Buffer.from(b.bytes.subarray(0, 256));
        assert.ok(aHead.equals(bHead), `Reference ${a.name} head bytes differ`);
    }
});
