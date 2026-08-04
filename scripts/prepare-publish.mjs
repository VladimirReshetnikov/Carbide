// SPDX-License-Identifier: Apache-2.0
//
// Publish-time dependency rewriting.
//
// `@carbide/cli` resolves its siblings through `file:` references so the workspace is usable
// without a registry. Those references cannot survive publication: npm would resolve
// `file:../core` relative to wherever the consumer installed the package, which is nowhere.
//
// This script swaps them for the published range and back again:
//
//   node scripts/prepare-publish.mjs            # file:../core  ->  ^0.1.0
//   node scripts/prepare-publish.mjs --restore  # ^0.1.0        ->  file:../core
//
// Publish between the two, and verify with `node scripts/check-publish.mjs --release`.
// Manifests are edited by targeted string replacement rather than a JSON round-trip so the
// diff stays limited to the lines that actually change.

import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const restore = process.argv.includes("--restore");

/** The published release train, keyed by package name. */
const publishedPackages = new Map([
    ["@carbide/core", "Carbide/packages/core"],
    ["@carbide/cli", "Carbide/packages/cli"],
    ["@carbide/msbuild-lite", "Carbide/packages/msbuild-lite"],
    ["@carbide/nuget", "Carbide/packages/nuget"],
    ["@carbide/refs-net10.0", "Carbide/packages/refs-net10.0"],
]);

const dependencyFields = ["dependencies", "devDependencies", "peerDependencies"];
const localSpecPattern = /^(?:file:|link:|workspace:|portal:)/;
const changes = [];
const errors = [];

/** The `file:` spec that points from one package directory to another. */
function localSpecFor(fromDirectory, toDirectory) {
    const relative = path
        .relative(path.join(repositoryRoot, fromDirectory), path.join(repositoryRoot, toDirectory))
        .replaceAll("\\", "/");
    return `file:${relative}`;
}

for (const [name, directory] of publishedPackages) {
    const manifestPath = path.join(repositoryRoot, directory, "package.json");
    let source = readFileSync(manifestPath, "utf8");
    const manifest = JSON.parse(source);
    let touched = false;

    for (const field of dependencyFields) {
        for (const [dependency, spec] of Object.entries(manifest[field] ?? {})) {
            const targetDirectory = publishedPackages.get(dependency);
            if (!targetDirectory) {
                // A non-train dependency with a local spec would be unresolvable once
                // published and cannot be rewritten to anything meaningful.
                if (localSpecPattern.test(spec)) {
                    errors.push(
                        `${directory}/package.json: ${field}.${dependency} uses the local spec ` +
                            `"${spec}" but ${dependency} is not published — it cannot be rewritten`,
                    );
                }
                continue;
            }

            const targetVersion = JSON.parse(
                readFileSync(path.join(repositoryRoot, targetDirectory, "package.json"), "utf8"),
            ).version;
            const wanted = restore ? localSpecFor(directory, targetDirectory) : `^${targetVersion}`;
            if (spec === wanted) {
                continue;
            }
            if (!restore && !localSpecPattern.test(spec)) {
                // Already a range. Keep it honest: it has to name the version being released.
                if (spec !== wanted) {
                    errors.push(
                        `${directory}/package.json: ${field}.${dependency} is "${spec}" but ` +
                            `${dependency} is at ${targetVersion} — expected "${wanted}"`,
                    );
                }
                continue;
            }

            const pattern = new RegExp(
                `("${dependency.replaceAll("/", "\\/").replaceAll(".", "\\.")}"\\s*:\\s*)"[^"]*"`,
            );
            if (!pattern.test(source)) {
                errors.push(`${directory}/package.json: could not locate the ${dependency} entry to rewrite`);
                continue;
            }
            source = source.replace(pattern, `$1"${wanted}"`);
            changes.push(`${directory}/package.json: ${field}.${dependency}  ${spec}  ->  ${wanted}`);
            touched = true;
        }
    }

    if (touched) {
        writeFileSync(manifestPath, source, "utf8");
    }
    void name;
}

if (errors.length > 0) {
    console.error("Publish preparation failed:\n");
    for (const error of errors) {
        console.error(`- ${error}`);
    }
    process.exitCode = 1;
} else if (changes.length === 0) {
    console.log(
        restore
            ? "Nothing to restore: every sibling dependency already uses its local spec."
            : "Nothing to prepare: no sibling dependency uses a local spec.",
    );
} else {
    console.log(restore ? "Restored local dependency specs:\n" : "Rewrote sibling dependencies for publish:\n");
    for (const change of changes) {
        console.log(`- ${change}`);
    }
    console.log(
        restore
            ? "\nRun `npm install` in the affected packages to relink the local siblings."
            : "\nVerify with `node scripts/check-publish.mjs --release`, publish, then re-run with --restore.",
    );
}
