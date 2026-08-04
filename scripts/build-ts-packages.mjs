// SPDX-License-Identifier: Apache-2.0
//
// Builds the pure-TypeScript half of the published packages, in dependency order, without
// touching the .NET toolchain. This is what the API-surface gate needs: `scripts/api-surface.mjs`
// reads emitted `.d.ts` files, and `@carbide/cli` resolves its siblings through `file:`
// references, so their `dist/` must exist before the CLI compiles.
//
// The Mono-WASM publish (`dotnet publish -c Release src/Carbide.Core.csproj`) is NOT run
// here — `@carbide/core`'s `build:ts` step compiles `src/ts/` alone and does not depend on
// it.
//
//   node scripts/build-ts-packages.mjs            # npm ci + build
//   node scripts/build-ts-packages.mjs --no-install  # build only (deps already installed)

import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const skipInstall = process.argv.includes("--no-install");

// Dependency order: the CLI's `file:` siblings must be built before it compiles.
const packages = [
    { directory: "Carbide/packages/msbuild-lite", build: "build" },
    { directory: "Carbide/packages/nuget", build: "build" },
    { directory: "Carbide/packages/core", build: "build:ts" },
    { directory: "Carbide/packages/cli", build: "build" },
];

// On Windows `npm` is a `.cmd` shim, and Node refuses to spawn `.cmd` without a shell
// (CVE-2024-27980). Pass the command as one string there rather than as an argv array —
// with `shell: true` an argv array is concatenated unescaped, which Node deprecates
// (DEP0190). Every token here is a literal from this file, never user input.
const useShell = process.platform === "win32";

function run(directory, args) {
    const cwd = path.join(repositoryRoot, directory);
    console.log(`\n> ${directory}: npm ${args.join(" ")}`);
    const result = useShell
        ? spawnSync(`npm ${args.join(" ")}`, { cwd, stdio: "inherit", shell: true })
        : spawnSync("npm", args, { cwd, stdio: "inherit" });
    if (result.error) {
        throw result.error;
    }
    if (result.status !== 0) {
        console.error(`\n${directory}: \`npm ${args.join(" ")}\` failed with exit code ${result.status}.`);
        process.exit(result.status ?? 1);
    }
}

for (const entry of packages) {
    if (!skipInstall) {
        const hasLock = existsSync(path.join(repositoryRoot, entry.directory, "package-lock.json"));
        run(entry.directory, hasLock ? ["ci"] : ["install"]);
    }
    run(entry.directory, ["run", entry.build]);
}

console.log(`\nBuilt ${packages.length} TypeScript packages.`);
