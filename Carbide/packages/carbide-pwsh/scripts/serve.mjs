// Static server for the carbide-pwsh demo. Root is the Carbide repo (not just this
// package) so the browser can reach:
//
//   /packages/carbide-pwsh/index.html             — the page
//   /packages/core/dist/index.js                  — the @carbide/core ESM entry
//   /packages/core/src/bin/.../wwwroot/_framework/* — Mono-WASM runtime + forked DLLs
//
// Structurally identical to packages/carbide-gh/scripts/serve.mjs.
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// From `.../packages/carbide-pwsh/scripts/` up 3 to the Carbide repo root.
export const REPO_ROOT = path.resolve(__dirname, "..", "..", "..");
export const DEMO_URL_PATH = "packages/carbide-pwsh/";
export const DEFAULT_PORT = Number(process.env.PORT ?? 34571);

export function guessMime(filePath) {
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

export function createCarbidePwshStaticServer({ repoRoot = REPO_ROOT, demoUrlPath = DEMO_URL_PATH } = {}) {
    return createServer(async (req, res) => {
        try {
            const url = new URL(req.url ?? "/", "http://127.0.0.1");
            let pathname = decodeURIComponent(url.pathname).replace(/^\/+/, "");
            if (pathname === "" ||
                pathname === demoUrlPath.replace(/\/$/, "") ||
                pathname === demoUrlPath) {
                pathname = demoUrlPath + "index.html";
            }
            const abs = path.resolve(repoRoot, pathname);
            if (!abs.startsWith(repoRoot)) {
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
}

export async function startCarbidePwshStaticServer({
    port = DEFAULT_PORT,
    host = "127.0.0.1",
    repoRoot = REPO_ROOT,
    demoUrlPath = DEMO_URL_PATH,
} = {}) {
    const server = createCarbidePwshStaticServer({ repoRoot, demoUrlPath });
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, host, () => {
            server.off("error", reject);
            resolve();
        });
    });

    const address = server.address();
    const actualPort = typeof address === "object" && address ? address.port : port;
    const url = `http://${host}:${actualPort}/${demoUrlPath}`;
    return { server, url, repoRoot, port: actualPort };
}

const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
    const { url } = await startCarbidePwshStaticServer({ port: DEFAULT_PORT });
    console.log(`carbide-pwsh demo server: ${url}`);
    console.log(`  repo root = ${REPO_ROOT}`);
}
