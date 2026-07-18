// SPDX-License-Identifier: Apache-2.0

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const apacheExpression = "Apache-2.0";
const errors = [];

const packageDirectories = [
    "Carbide.UI/packages/launcher",
    "Carbide.UI/packages/refs-avalonia",
    "Carbide.UI/packages/runner",
    "Carbide.UI/packages/runtime-bundle",
    "Carbide/packages/carbide-gh",
    "Carbide/packages/carbide-multishell",
    "Carbide/packages/carbide-pwsh",
    "Carbide/packages/cli",
    "Carbide/packages/core",
    "Carbide/packages/msbuild-lite",
    "Carbide/packages/nuget",
    "Carbide/packages/refs-net10.0",
];

const requiredNoticeFiles = [
    "Carbide/packages/core/ATTRIBUTION.md",
    "Carbide/packages/core/THIRD_PARTY_NOTICES.md",
    "Carbide/packages/core/third-party/dotnet/LICENSE.TXT",
    "Carbide/packages/core/third-party/dotnet/THIRD-PARTY-NOTICES.TXT",
    "Carbide/packages/core/third-party/dotnet-9/LICENSE.TXT",
    "Carbide/packages/core/third-party/dotnet-9/THIRD-PARTY-NOTICES.TXT",
    "Carbide/packages/core/third-party/dotnet-corefx-4.5/LICENSE.TXT",
    "Carbide/packages/core/third-party/dotnet-corefx-4.5/THIRD-PARTY-NOTICES.TXT",
    "Carbide/packages/core/third-party/dotnet-extensions-preview/LICENSE.TXT",
    "Carbide/packages/core/third-party/dotnet-extensions-preview/THIRD-PARTY-NOTICES.TXT",
    "Carbide/packages/core/third-party/jab/LICENSE",
    "Carbide/packages/core/third-party/roslyn/THIRD-PARTY-NOTICES.rtf",
    "Carbide/packages/core/third-party/roslyn-analyzers/THIRD-PARTY-NOTICES.txt",
    "Carbide/packages/core-bcl/System.Console/THIRD_PARTY_NOTICES.md",
    "Carbide/packages/carbide-gh/THIRD_PARTY_NOTICES.md",
    "Carbide/docs/reports/artifacts/carbide-gh-T21-artifact/THIRD_PARTY_NOTICES.md",
    "Carbide.UI/packages/refs-avalonia/THIRD_PARTY_NOTICES.md",
    "Carbide.UI/packages/runtime-bundle/THIRD_PARTY_NOTICES.md",
    "Carbide.UI/packages/runtime-bundle/third-party/avalonia/LICENSE.md",
    "Carbide.UI/packages/runtime-bundle/third-party/dotnet/LICENSE.TXT",
    "Carbide.UI/packages/runtime-bundle/third-party/dotnet/THIRD-PARTY-NOTICES.TXT",
    "Carbide.UI/packages/runtime-bundle/third-party/skiasharp-harfbuzzsharp/LICENSE.txt",
    "Carbide.UI/packages/runtime-bundle/third-party/skiasharp-harfbuzzsharp/THIRD-PARTY-NOTICES.txt",
    "Carbide.UI/packages/runtime-bundle/third-party/microcom/LICENSE",
    "Carbide/packages/refs-net10.0/third-party/dotnet/LICENSE.TXT",
    "Carbide/packages/refs-net10.0/third-party/dotnet/THIRD-PARTY-NOTICES.TXT",
];

const exactThirdPartyHashes = new Map([
    ["Carbide/packages/core/third-party/dotnet/LICENSE.TXT", "cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310"],
    ["Carbide/packages/core/third-party/dotnet/THIRD-PARTY-NOTICES.TXT", "66f1d4e44973185519bb4aa8a9718eb22fc7af2cc532e3ae9cfc4c127ee7fc54"],
    ["Carbide/packages/core/third-party/dotnet-9/LICENSE.TXT", "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b"],
    ["Carbide/packages/core/third-party/dotnet-9/THIRD-PARTY-NOTICES.TXT", "40686c6447a7d5b5d3693068e4571b5f483d7ed335aeee773ef662440de4c5d5"],
    ["Carbide/packages/core/third-party/dotnet-corefx-4.5/LICENSE.TXT", "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b"],
    ["Carbide/packages/core/third-party/dotnet-corefx-4.5/THIRD-PARTY-NOTICES.TXT", "7864a01e2fdef7e8fdf81b906efb1466f083206affea7ba7e6dadea429754765"],
    ["Carbide/packages/core/third-party/dotnet-extensions-preview/LICENSE.TXT", "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b"],
    ["Carbide/packages/core/third-party/dotnet-extensions-preview/THIRD-PARTY-NOTICES.TXT", "cde1f57820021ce44e184ef34ed5baccd635a43619960fd997a387ff5d8714db"],
    ["Carbide/packages/core/third-party/jab/LICENSE", "14e0606ac387bf8683f1a064dba6995ca7a290b822b133979f8a151443ac37e9"],
    ["Carbide/packages/core/third-party/roslyn/THIRD-PARTY-NOTICES.rtf", "ef4678850e7e3aaf9297f07e5c4060d477854e545ccf8e2ef8452d65a7b2efaf"],
    ["Carbide/packages/core/third-party/roslyn-analyzers/THIRD-PARTY-NOTICES.txt", "0eabe2880daf9bfb25ac160b1e779c5da95a88406dd06cdb570ec7edb5eabb76"],
    ["Carbide.UI/packages/runtime-bundle/third-party/avalonia/LICENSE.md", "213814d306090074d234d760239ff0f67eb9b8d20eefb4d5631bb39dbe0b769b"],
    ["Carbide.UI/packages/runtime-bundle/third-party/dotnet/LICENSE.TXT", "cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310"],
    ["Carbide.UI/packages/runtime-bundle/third-party/dotnet/THIRD-PARTY-NOTICES.TXT", "66f1d4e44973185519bb4aa8a9718eb22fc7af2cc532e3ae9cfc4c127ee7fc54"],
    ["Carbide.UI/packages/runtime-bundle/third-party/skiasharp-harfbuzzsharp/LICENSE.txt", "89101e35a8c66fd4d6dffc1763259161d35cb564c169714ec227a768c89f2938"],
    ["Carbide.UI/packages/runtime-bundle/third-party/skiasharp-harfbuzzsharp/THIRD-PARTY-NOTICES.txt", "21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5"],
    ["Carbide.UI/packages/runtime-bundle/third-party/microcom/LICENSE", "6ee769c9ac4dac9abb16b98b1341e9528ff9f4ab685481410d3376d14148f3a9"],
    ["Carbide/packages/refs-net10.0/third-party/dotnet/LICENSE.TXT", "d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b"],
    ["Carbide/packages/refs-net10.0/third-party/dotnet/THIRD-PARTY-NOTICES.TXT", "6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21"],
]);

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

const repositoryLicense = read("LICENSE");
assert(
    repositoryLicense.includes("Apache License\n                           Version 2.0, January 2004"),
    "LICENSE: expected the canonical Apache License 2.0 text",
);

const globalJson = JSON.parse(read("global.json"));
assert(globalJson.sdk?.version === "10.0.201", "global.json: SDK must remain pinned to 10.0.201 for the 10.0.6 WASM notices");
assert(globalJson.sdk?.rollForward === "disable", "global.json: SDK roll-forward must remain disabled for reproducible legal payloads");

const workingTreeFiles = execFileSync(
    "git",
    ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
    { cwd: repositoryRoot, encoding: "utf8" },
)
    .split("\0")
    .filter(Boolean)
    .map((relativePath) => relativePath.replaceAll("\\", "/"));

const scopedReadmes = workingTreeFiles.filter((relativePath) => path.posix.basename(relativePath) === "README.md");

const licenseFiles = workingTreeFiles.filter(
    (relativePath) => path.posix.basename(relativePath) === "LICENSE" && !relativePath.includes("/third-party/"),
);
assert(licenseFiles.includes("LICENSE"), "The repository root LICENSE is not tracked");

for (const relativePath of licenseFiles) {
    assert(read(relativePath) === repositoryLicense, `${relativePath}: must exactly match the root Apache-2.0 LICENSE`);
}

for (const directory of packageDirectories) {
    const manifestPath = `${directory}/package.json`;
    const manifest = JSON.parse(read(manifestPath));
    assert(manifest.license === apacheExpression, `${manifestPath}: license must be ${apacheExpression}`);
    assert(licenseFiles.includes(`${directory}/LICENSE`), `${directory}/LICENSE: package-local license copy is missing`);
}

for (const relativePath of scopedReadmes) {
    assert(/Apache(?: License)?(?:-| )2\.0/i.test(read(relativePath)), `${relativePath}: Apache-2.0 scope is not explicit`);
}

for (const relativePath of requiredNoticeFiles) {
    assert(read(relativePath).length > 0, `${relativePath}: required attribution or notice file is missing`);
}

for (const [relativePath, expectedHash] of exactThirdPartyHashes) {
    const bytes = readFileSync(path.join(repositoryRoot, relativePath));
    const actualHash = createHash("sha256").update(bytes).digest("hex");
    assert(actualHash === expectedHash, `${relativePath}: upstream payload hash mismatch (${actualHash})`);
}

const legalMarkdownFiles = [...new Set([
    ...scopedReadmes,
    ...requiredNoticeFiles.filter((relativePath) => relativePath.endsWith(".md")),
    "Carbide/packages/nuget/ATTRIBUTION.md",
])];
const markdownLinkPattern = /!?\[[^\]]*\]\(\s*(?:<([^>]+)>|([^\s)]+))/g;
for (const relativePath of legalMarkdownFiles) {
    const content = read(relativePath);
    for (const match of content.matchAll(markdownLinkPattern)) {
        const target = match[1] ?? match[2];
        if (/^(?:[a-z][a-z\d+.-]*:|#)/i.test(target)) {
            continue;
        }

        const localTarget = decodeURIComponent(target.split(/[?#]/, 1)[0]);
        const resolvedTarget = path.resolve(repositoryRoot, path.dirname(relativePath), localTarget);
        assert(existsSync(resolvedTarget), `${relativePath}: broken local link ${target}`);
    }
}

const coreManifest = JSON.parse(read("Carbide/packages/core/package.json"));
for (const entry of ["ATTRIBUTION.md", "THIRD_PARTY_NOTICES.md", "third-party/**/*"]) {
    assert(coreManifest.files?.includes(entry), `Carbide/packages/core/package.json: files must include ${entry}`);
}

const runtimeManifest = JSON.parse(read("Carbide.UI/packages/runtime-bundle/package.json"));
for (const entry of ["THIRD_PARTY_NOTICES.md", "third-party/**/*"]) {
    assert(runtimeManifest.files?.includes(entry), `Carbide.UI/packages/runtime-bundle/package.json: files must include ${entry}`);
}

const refsManifest = JSON.parse(read("Carbide/packages/refs-net10.0/package.json"));
assert(refsManifest.files?.includes("third-party/**/*"), "Carbide/packages/refs-net10.0/package.json: files must include third-party/**/*");

const nugetManifest = JSON.parse(read("Carbide/packages/nuget/package.json"));
assert(nugetManifest.files?.includes("ATTRIBUTION.md"), "Carbide/packages/nuget/package.json: files must include ATTRIBUTION.md");

for (const manifestPath of [
    "Carbide/packages/carbide-pwsh/package.json",
    "Carbide/packages/carbide-multishell/package.json",
]) {
    const manifest = JSON.parse(read(manifestPath));
    assert(Array.isArray(manifest.files) && manifest.files.length > 0, `${manifestPath}: defensive files allowlist is missing`);
}

const textFilePattern = /(?:^|\/)(?:LICENSE|README\.md|ATTRIBUTION\.md|THIRD[_-]PARTY[_-]NOTICES(?:\.[^.]+)?|package\.json|Directory\.Build\.props|\.editorconfig|\.gitattributes)$|\.(?:cs|ts|md)$/i;
for (const relativePath of workingTreeFiles.filter((candidate) => textFilePattern.test(candidate))) {
    const content = read(relativePath);
    assert(!/MIT(?: No Attribution|-0)/i.test(content), `${relativePath}: stale MIT-0 repository-license wording`);
    const contentWithoutHistoricalRepositoryUrls = content.replace(
        /https:\/\/github\.com\/VladimirReshetnikov\/Tools[^\s)>]*/gi,
        "",
    );
    assert(!/Vladimir/i.test(contentWithoutHistoricalRepositoryUrls), `${relativePath}: use Carbide Contributors instead of a personal name`);
}

const rootReadme = read("README.md");
const coreAttribution = read("Carbide/packages/core/ATTRIBUTION.md");
const wasmSharpCommit = "2f8c93bfa39f2068ad932a748ba23f740075327c";
assert(rootReadme.includes(wasmSharpCommit), "README.md: exact WasmSharp fork commit is missing");
assert(coreAttribution.includes(wasmSharpCommit), "Carbide/packages/core/ATTRIBUTION.md: exact WasmSharp fork commit is missing");
assert(rootReadme.includes("Carbide Contributors"), "README.md: collective copyright holder is missing");

if (errors.length > 0) {
    console.error("License and provenance validation failed:\n");
    for (const error of errors) {
        console.error(`- ${error}`);
    }
    process.exitCode = 1;
} else {
    console.log(
        `License and provenance validation passed: ${licenseFiles.length} Apache-2.0 LICENSE files, ` +
            `${packageDirectories.length} package manifests, ${scopedReadmes.length} scoped READMEs, and ` +
            `${requiredNoticeFiles.length} required notice files; local legal-document links are intact.`,
    );
}
