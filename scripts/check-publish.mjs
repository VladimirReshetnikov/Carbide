// SPDX-License-Identifier: Apache-2.0
//
// Publish-readiness gate.
//
// A package that builds and tests green can still be unusable once published: the tarball
// can omit its license or its notices, the manifest can be missing the metadata npm needs
// for provenance, or a dependency can point at a path that only exists in this working
// tree. This script asks npm what each package would actually ship and checks the answer.
//
//   node scripts/check-publish.mjs             # tarball contents + manifest metadata
//   node scripts/check-publish.mjs --release   # additionally: no local dependency specs
//
// `--release` is the pre-publish form. On `main` the CLI deliberately resolves its siblings
// through `file:` references so the workspace is usable without a registry, so the default
// form only asserts that those references point at packages in the release train. Run
// `scripts/prepare-publish.mjs` to rewrite them, then re-run with `--release`.

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const releaseMode = process.argv.includes("--release");
const errors = [];
const notes = [];

/**
 * Every published package, with the files its tarball must carry beyond the ones npm always
 * includes. `conditional` entries are only required when the source path exists in this
 * working tree — `_framework` is the Mono-WASM publish output, which a pure-TypeScript
 * checkout has not produced yet.
 */
const publishedPackages = [
    {
        directory: "Carbide/packages/core",
        name: "@carbide/core",
        required: [
            "CHANGELOG.md",
            "LICENSE",
            "README.md",
            "ATTRIBUTION.md",
            "THIRD_PARTY_NOTICES.md",
            "dist/index.js",
            "dist/index.d.ts",
            "dist/node.js",
            "dist/interop/schema.js",
        ],
        requiredPrefixes: ["third-party/"],
        conditional: [
            {
                sourcePath: "src/bin/Release/net10.0/publish/wwwroot/_framework",
                prefix: "src/bin/Release/net10.0/publish/wwwroot/_framework/",
                why: "the Mono-WASM runtime (run `dotnet publish -c Release src/Carbide.Core.csproj`)",
            },
        ],
    },
    {
        directory: "Carbide/packages/cli",
        name: "@carbide/cli",
        required: ["CHANGELOG.md", "LICENSE", "README.md", "dist/bin/carbide.js"],
    },
    {
        directory: "Carbide/packages/msbuild-lite",
        name: "@carbide/msbuild-lite",
        required: ["CHANGELOG.md", "LICENSE", "README.md", "dist/index.js", "dist/index.d.ts"],
    },
    {
        directory: "Carbide/packages/nuget",
        name: "@carbide/nuget",
        required: [
            "CHANGELOG.md",
            "LICENSE",
            "README.md",
            "ATTRIBUTION.md",
            "dist/index.js",
            "dist/index.d.ts",
        ],
    },
    {
        directory: "Carbide/packages/refs-net10.0",
        name: "@carbide/refs-net10.0",
        required: ["CHANGELOG.md", "LICENSE", "README.md", "THIRD_PARTY_NOTICES.md", "ref-manifest.json"],
        requiredPrefixes: ["third-party/"],
    },
];

const releaseTrain = new Set(publishedPackages.map((entry) => entry.name));
const localSpecPattern = /^(?:file:|link:|workspace:|portal:)/;

/**
 * C# projects whose `PackageReference`s end up as DLLs inside a published tarball —
 * `@carbide/core` ships the whole Mono-WASM `_framework` payload. A prerelease dependency
 * there is invisible to every npm-level check but very visible to a consumer, so it is
 * called out here rather than discovered after publish.
 */
const shippedProjects = [
    "Carbide/packages/core/src/Carbide.Core.csproj",
    "Carbide/packages/core-bcl/System.Console/Carbide.System.Console.csproj",
];

function assert(condition, message) {
    if (!condition) {
        errors.push(message);
    }
}

// On Windows `npm` is a `.cmd` shim that Node refuses to spawn without a shell
// (CVE-2024-27980); pass one string there so `shell: true` has nothing to concatenate.
const useShell = process.platform === "win32";

/** Ask npm for the tarball manifest without writing one. */
function packDryRun(directory) {
    const cwd = path.join(repositoryRoot, directory);
    const args = ["pack", "--dry-run", "--json"];
    const result = useShell
        ? spawnSync(`npm ${args.join(" ")}`, { cwd, encoding: "utf8", shell: true })
        : spawnSync("npm", args, { cwd, encoding: "utf8" });
    if (result.status !== 0) {
        errors.push(`${directory}: \`npm pack --dry-run\` failed:\n${result.stderr?.trim() ?? ""}`);
        return null;
    }
    // npm prints progress on stderr and the JSON document on stdout, but older versions
    // prepend a blank line — slice from the first bracket to stay tolerant.
    const start = result.stdout.indexOf("[");
    if (start < 0) {
        errors.push(`${directory}: \`npm pack --dry-run --json\` produced no JSON`);
        return null;
    }
    try {
        return JSON.parse(result.stdout.slice(start))[0];
    } catch (error) {
        errors.push(`${directory}: could not parse the pack manifest (${error.message})`);
        return null;
    }
}

for (const entry of publishedPackages) {
    const manifestPath = `${entry.directory}/package.json`;
    const manifest = JSON.parse(readFileSync(path.join(repositoryRoot, manifestPath), "utf8"));

    assert(manifest.name === entry.name, `${manifestPath}: name is "${manifest.name}", expected ${entry.name}`);
    assert(manifest.private !== true, `${manifestPath}: a published package must not be private`);

    // Metadata npm and its provenance tooling need. Missing pieces degrade the package page
    // and break `npm repo` / provenance attestation rather than failing the publish, which
    // is exactly why they are worth asserting here.
    for (const field of ["description", "license", "version"]) {
        assert(
            typeof manifest[field] === "string" && manifest[field].length > 0,
            `${manifestPath}: ${field} is required`,
        );
    }
    assert(
        Array.isArray(manifest.keywords) && manifest.keywords.length > 0,
        `${manifestPath}: keywords are required`,
    );
    assert(manifest.engines?.node === ">=20", `${manifestPath}: engines.node must be ">=20"`);
    assert(
        manifest.repository?.url === "git+https://github.com/VladimirReshetnikov/Carbide.git",
        `${manifestPath}: repository.url must be the canonical repository URL`,
    );
    assert(
        manifest.repository?.directory === entry.directory,
        `${manifestPath}: repository.directory must be "${entry.directory}", got "${manifest.repository?.directory}"`,
    );

    // Dependency specs. A `file:` reference is fine in the working tree — it is how the CLI
    // resolves its siblings without a registry — but it must target a package that is
    // published alongside it, and it must be gone by publish time.
    for (const [dependency, spec] of Object.entries(manifest.dependencies ?? {})) {
        if (!localSpecPattern.test(spec)) {
            continue;
        }
        if (releaseMode) {
            errors.push(
                `${manifestPath}: dependency ${dependency} is still "${spec}" — run ` +
                    "`node scripts/prepare-publish.mjs` before publishing",
            );
            continue;
        }
        assert(
            releaseTrain.has(dependency),
            `${manifestPath}: dependency ${dependency} uses the local spec "${spec}" but is not in ` +
                "the release train, so a published install could never resolve it",
        );
    }

    const packed = packDryRun(entry.directory);
    if (!packed) {
        continue;
    }
    const packedPaths = new Set(packed.files.map((file) => file.path.replaceAll("\\", "/")));

    for (const required of entry.required) {
        assert(
            packedPaths.has(required),
            `${entry.name}: the published tarball would not contain ${required}`,
        );
    }
    for (const prefix of entry.requiredPrefixes ?? []) {
        assert(
            [...packedPaths].some((packedPath) => packedPath.startsWith(prefix)),
            `${entry.name}: the published tarball would not contain anything under ${prefix}`,
        );
    }
    for (const conditional of entry.conditional ?? []) {
        const sourceExists = existsSync(path.join(repositoryRoot, entry.directory, conditional.sourcePath));
        if (!sourceExists) {
            notes.push(
                `${entry.name}: skipped the ${conditional.prefix} check — not built in this tree. ` +
                    `A release build needs ${conditional.why}.`,
            );
            continue;
        }
        assert(
            [...packedPaths].some((packedPath) => packedPath.startsWith(conditional.prefix)),
            `${entry.name}: ${conditional.sourcePath} exists but the tarball would not carry ` +
                `${conditional.prefix} — check the files allow-list`,
        );
    }
}

for (const projectPath of shippedProjects) {
    const absolute = path.join(repositoryRoot, projectPath);
    if (!existsSync(absolute)) {
        errors.push(`${projectPath}: shipped project is missing`);
        continue;
    }
    const project = readFileSync(absolute, "utf8");
    for (const match of project.matchAll(/<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"/g)) {
        const [, id, version] = match;
        // SemVer says everything after the first `-` is a prerelease label.
        if (version.includes("-")) {
            errors.push(
                `${projectPath}: ${id} is pinned to the prerelease ${version}. Its assembly ships in ` +
                    "@carbide/core's _framework payload, so a release must use a stable version.",
            );
        }
    }
}

for (const note of notes) {
    console.log(`note: ${note}`);
}

if (errors.length > 0) {
    console.error("\nPublish-readiness validation failed:\n");
    for (const error of errors) {
        console.error(`- ${error}`);
    }
    process.exitCode = 1;
} else {
    console.log(
        `\nPublish-readiness validation passed: ${publishedPackages.length} tarballs carry their ` +
            `license, notices, changelog, and build output` +
            (releaseMode ? ", and no local dependency specs remain." : "."),
    );
}
