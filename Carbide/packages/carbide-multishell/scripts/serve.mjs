// Static server for the carbide-multishell demo. Root is the Carbide repo (not just this
// package) so the browser can reach:
//
//   /packages/carbide-multishell/index.html          — the page
//   /packages/core/dist/index.js                     — the @carbide/core ESM entry
//   /packages/core/src/bin/.../wwwroot/_framework/*  — Mono-WASM runtime + forked DLLs
//   /packages/carbide-{shell-core,pwsh,cmd,bash}/src/**/*.cs — source files fetched by the page
//
// Structurally identical to the carbide-pwsh serve script, just a different URL path.
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// From `.../packages/carbide-multishell/scripts/` up 3 to the Carbide repo root.
const REPO_ROOT = path.resolve(__dirname, "..", "..", "..");
const DEMO_URL_PATH = "packages/carbide-multishell/";
const PORT = Number(process.env.PORT ?? 34572);

function guessMime(filePath) {
    const ext = path.extname(filePath).toLowerCase();
    switch (ext) {
        case ".wasm": return "application/wasm";
        case ".js":
        case ".mjs": return "text/javascript; charset=utf-8";
        case ".html": return "text/html; charset=utf-8";
        case ".css": return "text/css; charset=utf-8";
        case ".json": return "application/json; charset=utf-8";
        case ".cs": return "text/plain; charset=utf-8";
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
            pathname === DEMO_URL_PATH.replace(/\/$/, "") ||
            pathname === DEMO_URL_PATH) {
            pathname = DEMO_URL_PATH + "index.html";
        }
        const abs = path.resolve(REPO_ROOT, pathname);
        if (!abs.startsWith(REPO_ROOT)) {
            res.writeHead(403); res.end("Forbidden"); return;
        }
        const st = await stat(abs);
        const filePath = st.isDirectory() ? path.join(abs, "index.html") : abs;
        if (st.isDirectory()) {
            const indexStat = await stat(filePath);
            if (!indexStat.isFile()) {
                res.writeHead(404); res.end("Directory listings disabled"); return;
            }
        }
        const data = await readFile(filePath);
        res.writeHead(200, {
            "content-type": guessMime(filePath),
            "cache-control": "no-store",
            "access-control-allow-origin": "*",
        });
        res.end(data);
    } catch {
        res.writeHead(404); res.end("Not found");
    }
});

server.listen(PORT, "127.0.0.1", () => {
    const url = `http://127.0.0.1:${PORT}/${DEMO_URL_PATH}`;
    console.log(`carbide-multishell demo server: ${url}`);
    console.log(`  repo root = ${REPO_ROOT}`);
});
