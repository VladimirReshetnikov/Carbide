// Ported from cs-agent-tools/cs-validate/src/wasmsharp/asset-server.ts with Carbide-namespace
// naming. The security posture (path-traversal guard, MIME by extension) must match that source
// byte-for-byte; changes here should be reflected back into cs-agent-tools or flagged as a
// deliberate divergence in the PR description.

import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { readFile } from "node:fs/promises";
import path from "node:path";

export type AssetServerHandle = {
    /** Base URL (always ends with a trailing slash). */
    readonly baseUrl: string;
    close(): Promise<void>;
};

export interface AssetServerOptions {
    /** Called for each 4xx/5xx; useful for tying server diagnostics into a session's debug log. */
    logNotice?(message: string): void;
}

function guessMime(filePath: string): string {
    const ext = path.extname(filePath).toLowerCase();
    if (ext === ".wasm") return "application/wasm";
    if (ext === ".js" || ext === ".mjs") return "text/javascript; charset=utf-8";
    if (ext === ".json") return "application/json; charset=utf-8";
    if (ext === ".dat") return "application/octet-stream";
    if (ext === ".dll") return "application/octet-stream";
    return "application/octet-stream";
}

function safeDecodePath(p: string): string {
    try {
        return decodeURIComponent(p);
    } catch {
        return p;
    }
}

async function handle(
    req: IncomingMessage,
    res: ServerResponse,
    rootDir: string,
    logNotice?: (message: string) => void,
): Promise<void> {
    const url = new URL(req.url ?? "/", "http://127.0.0.1");
    const pathname = safeDecodePath(url.pathname);
    if (pathname.includes("\0")) {
        res.writeHead(400);
        res.end("Bad path");
        logNotice?.(`400 bad path: ${pathname}`);
        return;
    }

    const rel = pathname.replace(/^\/+/, "");
    const abs = path.resolve(rootDir, rel);
    const rootAbs = path.resolve(rootDir);
    if (!abs.startsWith(rootAbs)) {
        res.writeHead(403);
        res.end("Forbidden");
        logNotice?.(`403 traversal: ${pathname}`);
        return;
    }

    try {
        const data = await readFile(abs);
        res.writeHead(200, {
            "content-type": guessMime(abs),
            "cache-control": "public, max-age=31536000, immutable",
        });
        res.end(data);
    } catch {
        res.writeHead(404);
        res.end("Not found");
        logNotice?.(`404 missing: ${pathname}`);
    }
}

/**
 * Starts a localhost HTTP server that serves `rootDir`. Carbide's Node host adapter does not
 * require this for the M1 happy path (the .NET WASM runtime's built-in fetch shim reads
 * file:// URLs directly from disk), but the server is exported so callers with stricter
 * content-type or CORS needs — and later multi-worker scenarios — can opt in.
 */
export async function startAssetServer(
    rootDir: string,
    options: AssetServerOptions = {},
): Promise<AssetServerHandle> {
    const server = createServer((req, res) => {
        void handle(req, res, rootDir, options.logNotice);
    });

    await new Promise<void>((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", () => resolve());
    });

    server.unref();

    const addr = server.address();
    if (!addr || typeof addr === "string") {
        throw new Error("Failed to bind local asset server");
    }

    const baseUrl = `http://127.0.0.1:${addr.port}/`;

    return {
        baseUrl,
        close: () =>
            new Promise<void>((resolve) => {
                server.close(() => resolve());
            }),
    };
}
