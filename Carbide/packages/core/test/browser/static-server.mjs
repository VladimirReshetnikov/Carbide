// Tiny static-file server used by Playwright's webServer. Serves the package root so the
// browser can load dist/*.js and src/bin/.../wwwroot/_framework/*.
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const PORT = Number(process.env.PORT ?? 34567);

function guessMime(filePath) {
    const ext = path.extname(filePath).toLowerCase();
    if (ext === ".wasm") return "application/wasm";
    if (ext === ".js" || ext === ".mjs") return "text/javascript; charset=utf-8";
    if (ext === ".html") return "text/html; charset=utf-8";
    if (ext === ".json") return "application/json; charset=utf-8";
    if (ext === ".dat") return "application/octet-stream";
    if (ext === ".dll") return "application/octet-stream";
    return "application/octet-stream";
}

const server = createServer(async (req, res) => {
    try {
        const url = new URL(req.url ?? "/", `http://127.0.0.1:${PORT}`);
        const pathname = decodeURIComponent(url.pathname).replace(/^\/+/, "");
        const abs = path.resolve(PACKAGE_ROOT, pathname);
        if (!abs.startsWith(PACKAGE_ROOT)) {
            res.writeHead(403);
            res.end("Forbidden");
            return;
        }
        const st = await stat(abs);
        if (st.isDirectory()) {
            res.writeHead(404);
            res.end("Directory listing not served");
            return;
        }
        const data = await readFile(abs);
        res.writeHead(200, {
            "content-type": guessMime(abs),
            "cache-control": "no-store",
        });
        res.end(data);
    } catch {
        res.writeHead(404);
        res.end("Not found");
    }
});

server.listen(PORT, "127.0.0.1", () => {
    console.log(`carbide static server: http://127.0.0.1:${PORT} root=${PACKAGE_ROOT}`);
});
