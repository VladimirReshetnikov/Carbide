// SPDX-License-Identifier: Apache-2.0
//
// M7 — release-metadata gate.
//
// Carbide publishes five packages in lock-step at a single version. This script asserts
// that the version is genuinely single-sourced, that every published package documents its
// release, and that the changelogs stay in a machine-checkable shape:
//
//   - every publishable package carries a CHANGELOG.md in Keep a Changelog form,
//     ships it in the npm tarball, and heads it with an `## [Unreleased]` section;
//   - the topmost released version in each changelog equals that package's
//     package.json version, and released versions descend without duplicates;
//   - the repository changelog agrees with the packages;
//   - package.json versions, the repository changelog, and `CARBIDE_VERSION` all agree.
//
//   node scripts/check-changelog.mjs

import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const errors = [];

/** The published release train. `Carbide.UI` packages are private and excluded on purpose. */
const publishedPackages = [
    "Carbide/packages/core",
    "Carbide/packages/cli",
    "Carbide/packages/msbuild-lite",
    "Carbide/packages/nuget",
    "Carbide/packages/refs-net10.0",
];

/** Section headings Keep a Changelog defines. Anything else is a typo or an invention. */
const changeSections = new Set([
    "Added",
    "Changed",
    "Deprecated",
    "Removed",
    "Fixed",
    "Security",
    // Carbide addition: caveats that are neither a change nor a fix, but belong with the
    // release (documented here so the vocabulary stays closed).
    "Notes",
]);

const semverPattern = /^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$/;

function read(relativePath) {
    try {
        return readFileSync(path.join(repositoryRoot, relativePath), "utf8");
    } catch (error) {
        errors.push(`${relativePath}: cannot read file (${error.code ?? error.message})`);
        return "";
    }
}

function assert(condition, message) {
    if (!condition) {
        errors.push(message);
    }
}

/** Compare two semver strings. Pre-release versions sort below their release. */
function compareVersions(a, b) {
    const left = semverPattern.exec(a);
    const right = semverPattern.exec(b);
    if (!left || !right) return 0;
    for (let index = 1; index <= 3; index++) {
        const delta = Number(left[index]) - Number(right[index]);
        if (delta !== 0) return delta;
    }
    if (left[4] === right[4]) return 0;
    if (!left[4]) return 1;
    if (!right[4]) return -1;
    return left[4] < right[4] ? -1 : 1;
}

/**
 * Parse a changelog into its version headings. Recognised forms:
 *   `## [Unreleased]`
 *   `## [1.2.3] - 2026-08-04`
 */
function parseChangelog(relativePath, content) {
    const lines = content.split(/\r?\n/);
    assert(
        lines[0]?.startsWith("# Changelog"),
        `${relativePath}: must start with a "# Changelog" heading`,
    );
    assert(
        /keepachangelog\.com/.test(content),
        `${relativePath}: must state that it follows Keep a Changelog`,
    );
    assert(
        /semver\.org/.test(content),
        `${relativePath}: must state that it follows Semantic Versioning`,
    );

    const releases = [];
    let sawUnreleased = false;
    let currentRelease = null;

    for (const [index, line] of lines.entries()) {
        const heading = /^## \[([^\]]+)\](?:\s*-\s*(\S+))?\s*$/.exec(line);
        if (heading) {
            const [, label, date] = heading;
            if (label === "Unreleased") {
                assert(
                    releases.length === 0,
                    `${relativePath}:${index + 1}: [Unreleased] must be the first version heading`,
                );
                sawUnreleased = true;
                currentRelease = null;
                continue;
            }
            assert(
                semverPattern.test(label),
                `${relativePath}:${index + 1}: "${label}" is not a semantic version`,
            );
            assert(
                date && /^\d{4}-\d{2}-\d{2}$/.test(date),
                `${relativePath}:${index + 1}: [${label}] needs an ISO date (## [${label}] - YYYY-MM-DD)`,
            );
            currentRelease = { version: label, date, line: index + 1 };
            releases.push(currentRelease);
            continue;
        }

        const section = /^### (.+?)\s*$/.exec(line);
        if (section) {
            assert(
                changeSections.has(section[1]),
                `${relativePath}:${index + 1}: "${section[1]}" is not a Keep a Changelog section ` +
                    `(${[...changeSections].join(", ")})`,
            );
            assert(
                currentRelease !== null || sawUnreleased,
                `${relativePath}:${index + 1}: "${section[1]}" appears before any version heading`,
            );
        }
    }

    assert(sawUnreleased, `${relativePath}: must carry an "## [Unreleased]" section`);
    assert(releases.length > 0, `${relativePath}: must document at least one release`);

    for (let index = 1; index < releases.length; index++) {
        assert(
            compareVersions(releases[index - 1].version, releases[index].version) > 0,
            `${relativePath}:${releases[index].line}: releases must descend — ` +
                `[${releases[index - 1].version}] is listed above [${releases[index].version}]`,
        );
    }

    // Every version heading is a reference link, so it needs a definition at the bottom.
    for (const release of [{ version: "Unreleased" }, ...releases]) {
        assert(
            new RegExp(`^\\[${release.version.replaceAll(".", "\\.")}\\]:\\s*\\S+`, "m").test(content),
            `${relativePath}: missing link definition for [${release.version}]`,
        );
    }

    return releases;
}

const packageVersions = new Map();

for (const directory of publishedPackages) {
    const manifestPath = `${directory}/package.json`;
    const manifestText = read(manifestPath);
    if (!manifestText) continue;
    const manifest = JSON.parse(manifestText);

    assert(
        semverPattern.test(manifest.version ?? ""),
        `${manifestPath}: version "${manifest.version}" is not a semantic version`,
    );
    packageVersions.set(directory, manifest.version);

    // npm auto-includes README and LICENSE but not CHANGELOG, so a `files` allow-list has
    // to name it or the published tarball ships without release notes.
    assert(
        Array.isArray(manifest.files) && manifest.files.includes("CHANGELOG.md"),
        `${manifestPath}: files must include CHANGELOG.md so it ships in the tarball`,
    );

    const changelogPath = `${directory}/CHANGELOG.md`;
    const changelog = read(changelogPath);
    if (!changelog) continue;
    const releases = parseChangelog(changelogPath, changelog);
    if (releases.length === 0) continue;

    assert(
        releases[0].version === manifest.version,
        `${changelogPath}: newest release is [${releases[0].version}] but ${manifestPath} says ` +
            `${manifest.version} — release the changelog entry and the version together`,
    );
}

// Lock-step: one version across the whole train.
const distinctVersions = [...new Set(packageVersions.values())];
assert(
    distinctVersions.length <= 1,
    `published packages must share one version, found: ${[...packageVersions]
        .map(([directory, version]) => `${directory}@${version}`)
        .join(", ")}`,
);
const releaseVersion = distinctVersions[0];

// `CARBIDE_VERSION` is a literal (browser bundles cannot import package.json), so it is the
// one copy that can silently drift.
const versionTs = read("Carbide/packages/core/src/ts/version.ts");
const carbideVersion = /export const CARBIDE_VERSION = "([^"]+)" as const;/.exec(versionTs);
if (!carbideVersion) {
    errors.push("Carbide/packages/core/src/ts/version.ts: could not find the CARBIDE_VERSION literal");
} else {
    assert(
        carbideVersion[1] === releaseVersion,
        `Carbide/packages/core/src/ts/version.ts: CARBIDE_VERSION is "${carbideVersion[1]}" but the ` +
            `packages are at ${releaseVersion}`,
    );
}

const rootChangelog = read("CHANGELOG.md");
if (rootChangelog) {
    const releases = parseChangelog("CHANGELOG.md", rootChangelog);
    if (releases.length > 0 && releaseVersion) {
        assert(
            releases[0].version === releaseVersion,
            `CHANGELOG.md: newest release is [${releases[0].version}] but the packages are at ${releaseVersion}`,
        );
    }
    for (const directory of publishedPackages) {
        assert(
            rootChangelog.includes(`${directory}/CHANGELOG.md`),
            `CHANGELOG.md: must link to ${directory}/CHANGELOG.md`,
        );
    }
}

if (errors.length > 0) {
    console.error("Changelog and release-metadata validation failed:\n");
    for (const error of errors) {
        console.error(`- ${error}`);
    }
    process.exitCode = 1;
} else {
    console.log(
        `Changelog validation passed: ${publishedPackages.length} published packages plus the ` +
            `repository changelog agree on version ${releaseVersion}.`,
    );
}
