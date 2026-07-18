// Multi-root static server for @carbide-ui/launcher browser tests.
//
// Mounts the repository root so the browser can reach both top-level project trees
// via absolute URLs: /Carbide/packages/core/..., /Carbide.UI/packages/launcher/... etc.
// No CORS / caching headers — same-origin everything; `cache-control: no-store`
// matches Carbide core's pattern.

import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
// HERE = Carbide.UI/packages/launcher/test/browser.
// Walk up 5 levels to reach the repository root (Carbide.UI is top-level).
const REPOSITORY_ROOT = path.resolve(HERE, "..", "..", "..", "..", "..");
const PORT = Number(process.env.PORT ?? 34568);

function guessMime(filePath) {
    const ext = path.extname(filePath).toLowerCase();
    if (ext === ".wasm") return "application/wasm";
    if (ext === ".js" || ext === ".mjs") return "text/javascript; charset=utf-8";
    if (ext === ".html") return "text/html; charset=utf-8";
    if (ext === ".json") return "application/json; charset=utf-8";
    if (ext === ".css") return "text/css; charset=utf-8";
    if (ext === ".dll" || ext === ".dat") return "application/octet-stream";
    return "application/octet-stream";
}

// Scoped-package name shim: map `@carbide-ui/<pkg>/...` to the monorepo's
// `Carbide.UI/packages/<pkg>/...` layout so the fixture can exercise sideload with
// the real scoped npm name. Real deployments don't need this — production consumers
// install into node_modules where the scoped path already exists.
const SCOPED_NAME_SHIMS = [
    { prefix: "/scoped/@carbide-ui/", rewriteTo: "/Carbide.UI/packages/" },
];

function applyScopedShim(pathname) {
    for (const shim of SCOPED_NAME_SHIMS) {
        const withSlash = pathname.startsWith("/") ? pathname : `/${pathname}`;
        if (withSlash.startsWith(shim.prefix)) {
            return shim.rewriteTo.replace(/^\//, "") + withSlash.slice(shim.prefix.length);
        }
    }
    return pathname;
}

const server = createServer(async (req, res) => {
    try {
        const url = new URL(req.url ?? "/", `http://127.0.0.1:${PORT}`);
        const rawPathname = decodeURIComponent(url.pathname).replace(/^\/+/, "");
        const pathname = applyScopedShim(rawPathname);
        const abs = path.resolve(REPOSITORY_ROOT, pathname);
        if (!abs.startsWith(REPOSITORY_ROOT)) {
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
    console.log(`carbide-ui static server: http://127.0.0.1:${PORT} root=${REPOSITORY_ROOT}`);
});
