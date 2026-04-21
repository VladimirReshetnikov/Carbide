import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import http from "node:http";
import { startAssetServer } from "../../dist/node.js";

/**
 * Send a raw HTTP GET with an exact request path (no WHATWG-URL normalisation).
 * Needed for the traversal check — both fetch() and new URL() collapse "../" segments
 * before they reach the server, hiding whether the server guards against them.
 */
function rawGet(port, rawPath) {
    return new Promise((resolve, reject) => {
        const req = http.request({ hostname: "127.0.0.1", port, path: rawPath, method: "GET" }, (res) => {
            const chunks = [];
            res.on("data", (c) => chunks.push(c));
            res.on("end", () => resolve({ status: res.statusCode, body: Buffer.concat(chunks).toString() }));
        });
        req.on("error", reject);
        req.end();
    });
}

test("asset server serves within-root files, rejects null-byte paths, does not leak siblings", async () => {
    const dir = mkdtempSync(path.join(tmpdir(), "carbide-asset-"));
    writeFileSync(path.join(dir, "ok.txt"), "ok");
    // A sibling file outside rootDir that must stay inaccessible regardless of path shape.
    const sibling = path.join(path.dirname(dir), `evil-${path.basename(dir)}.txt`);
    writeFileSync(sibling, "should-not-be-served");

    const handle = await startAssetServer(dir);
    const port = Number(new URL(handle.baseUrl).port);
    try {
        const okResp = await fetch(`${handle.baseUrl}ok.txt`);
        assert.equal(okResp.status, 200);
        assert.equal(await okResp.text(), "ok");

        // Null byte hits the 400 guard directly; this is our proof the guard path is live.
        const nullResp = await rawGet(port, "/%00bad");
        assert.equal(nullResp.status, 400, `expected 400 for null-byte path, got ${nullResp.status}`);

        // Even with a full path injected, the server must never leak a sibling file's contents.
        const leakResp = await fetch(`${handle.baseUrl}${encodeURIComponent(path.basename(sibling))}`);
        assert.notEqual(await leakResp.text(), "should-not-be-served", "sibling outside rootDir leaked");

        const missingResp = await fetch(`${handle.baseUrl}nothere`);
        assert.equal(missingResp.status, 404);
    } finally {
        await handle.close();
        rmSync(dir, { recursive: true, force: true });
        rmSync(sibling, { force: true });
    }
});

// Regression for review R1 C4 / R2 §5 — the older `abs.startsWith(rootAbs)` guard let
// sibling directories with the same string prefix escape through, because string prefix
// isn't a path-boundary check. A root like `/tmp/root` prefixes `/tmp/root-evil/secret`.
// The fixed guard uses `path.relative` and rejects any `..` escape.
test("asset server rejects prefix-sibling traversal", async () => {
    const parentDir = mkdtempSync(path.join(tmpdir(), "carbide-asset-parent-"));
    const rootDir = path.join(parentDir, "root");
    const siblingDir = path.join(parentDir, "root-evil");
    writeFileSync(path.join(parentDir, "dummy.txt"), "parent-file-must-not-leak");
    // Use mkdirSync via fs for the nested dirs.
    const { mkdirSync } = await import("node:fs");
    mkdirSync(rootDir);
    mkdirSync(siblingDir);
    writeFileSync(path.join(rootDir, "ok.txt"), "ok");
    writeFileSync(path.join(siblingDir, "secret.txt"), "should-not-be-served");

    const handle = await startAssetServer(rootDir);
    const port = Number(new URL(handle.baseUrl).port);
    try {
        // Sanity: within-root request works.
        const okResp = await fetch(`${handle.baseUrl}ok.txt`);
        assert.equal(okResp.status, 200);
        assert.equal(await okResp.text(), "ok");

        // The attack path uses percent-encoded slashes (`%2F`) to smuggle `..` segments
        // past Node's URL class. `new URL("/%2E%2E/x")` normalises `%2E%2E` away, but
        // `new URL("/a%2F..%2F..%2Fx")` keeps the whole thing as one opaque percent-
        // encoded segment — our `safeDecodePath` then decodes the `%2F` to `/`, and the
        // `..` segments survive into `path.resolve`. With root=`<parent>/root`, the
        // resolved `abs` becomes `<parent>/root-evil/secret.txt`, which shares the
        // `<parent>/root` prefix — that's the exact bug the OLD `abs.startsWith(rootAbs)`
        // guard missed. The NEW `path.relative` guard sees `..` + sep and returns 403.
        const attack = "/a%2F..%2F..%2Froot-evil/secret.txt";
        const attackResp = await rawGet(port, attack);
        assert.equal(attackResp.status, 403,
            `prefix-sibling traversal via ${attack} must be forbidden (got ${attackResp.status})`);

        // Also confirm we can't leak the parent-directory's file.
        const parentAttack = "/a%2F..%2F..%2Fdummy.txt";
        const parentResp = await rawGet(port, parentAttack);
        assert.equal(parentResp.status, 403,
            `parent-escape via ${parentAttack} must be forbidden (got ${parentResp.status})`);
    } finally {
        await handle.close();
        rmSync(parentDir, { recursive: true, force: true });
    }
});
