// Static server for the carbide-gh T2.1 investigation artifact. Root is the Carbide
// repo (not just this artifact) so the browser can reach:
//
//   /docs/reports/artifacts/carbide-gh-T21-artifact/index.html  — the page
//   /packages/core/dist/index.js                                — the @carbide/core ESM entry
//   /packages/core/src/bin/.../wwwroot/_framework/*             — Mono-WASM runtime + forked DLLs
//
// No caching, no directory listings, single process. Press Ctrl+C to stop.
//
// Artifact note: this script is preserved as part of the T2.1 report artifact. The
// page it serves is NOT a working demo — it boots, renders the banner, then trips
// "Cannot wait on monitors" on the first await. See ../README.md.
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// From `.../docs/reports/artifacts/carbide-gh-T21-artifact/scripts/` up 5 to the Carbide
// repo root. (T2 pre-move path was 3 levels; kept as a reminder if the artifact ever
// moves back out of docs/reports/artifacts/.)
const REPO_ROOT = path.resolve(__dirname, "..", "..", "..", "..", "..");
const ARTIFACT_URL_PATH = "docs/reports/artifacts/carbide-gh-T21-artifact/";
const PORT = Number(process.env.PORT ?? 34570);

function guessMime(filePath) {
    const ext = path.extname(filePath).toLowerCase();
    switch (ext) {
        case ".wasm": return "application/wasm";
        case ".js":
        case ".mjs": return "text/javascript; charset=utf-8";
        case ".html": return "text/html; charset=utf-8";
        case ".css": return "text/css; charset=utf-8";
        case ".json": return "application/json; charset=utf-8";
        case ".svg": return "image/svg+xml";
        case ".dll":
        case ".pdb":
        case ".dat": return "application/octet-stream";
        default: return "application/octet-stream";
    }
}

const server = createServer(async (req, res) => {
    try {
        const url = new URL(req.url ?? "/", `http://127.0.0.1:${PORT}`);
        let pathname = decodeURIComponent(url.pathname).replace(/^\/+/, "");
        if (pathname === "" ||
            pathname === ARTIFACT_URL_PATH.replace(/\/$/, "") ||
            pathname === ARTIFACT_URL_PATH) {
            pathname = ARTIFACT_URL_PATH + "index.html";
        }
        const abs = path.resolve(REPO_ROOT, pathname);
        if (!abs.startsWith(REPO_ROOT)) {
            res.writeHead(403); res.end("Forbidden"); return;
        }
        const st = await stat(abs);
        if (st.isDirectory()) {
            res.writeHead(404); res.end("Directory listings disabled"); return;
        }
        const data = await readFile(abs);
        res.writeHead(200, {
            "content-type": guessMime(abs),
            "cache-control": "no-store",
            // Carbide's forked Mono-WASM doesn't need cross-origin isolation, but don't
            // hurt to declare a permissive CORS here for the demo.
            "access-control-allow-origin": "*",
        });
        res.end(data);
    } catch {
        res.writeHead(404); res.end("Not found");
    }
});

server.listen(PORT, "127.0.0.1", () => {
    const url = `http://127.0.0.1:${PORT}/${ARTIFACT_URL_PATH}`;
    console.log(`carbide-gh T2.1 artifact server: ${url}`);
    console.log(`  repo root = ${REPO_ROOT}`);
    console.log(`  note: this artifact is NOT a working demo. See ../README.md.`);
});
