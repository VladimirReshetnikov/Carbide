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
