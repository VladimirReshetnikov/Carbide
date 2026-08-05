// `CARBIDE_ROSLYN_VERSION` decides which `analyzers/dotnet/roslyn<X.Y>/` folder a package's
// generator is loaded from. If it drifts from the Roslyn version Carbide actually compiles
// with, nothing fails: selection just starts choosing a folder built against a different
// analyzer API, and the generator throws at run time — or, worse, silently stops matching a
// folder it used to match and contributes nothing.
//
// This reads the version out of Carbide.Core.csproj so a Roslyn upgrade cannot land without
// updating the constant.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { CARBIDE_ROSLYN_VERSION } from "../../dist/index.js";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CSPROJ = path.resolve(HERE, "../../../core/src/Carbide.Core.csproj");

test("CARBIDE_ROSLYN_VERSION matches Carbide.Core's Microsoft.CodeAnalysis.CSharp reference", () => {
    if (!existsSync(CSPROJ)) {
        // Consumer checkouts of the published tarball have no sibling core package. The
        // constant is still correct for the runtime that shipped with it.
        assert.ok(true, `${CSPROJ} not present; skipping repo-layout assertion.`);
        return;
    }
    const xml = readFileSync(CSPROJ, "utf8");
    const match = /<PackageReference\s+Include="Microsoft\.CodeAnalysis\.CSharp"\s+Version="(\d+)\.(\d+)\.[^"]*"/.exec(xml);
    assert.ok(match, "could not find the Microsoft.CodeAnalysis.CSharp PackageReference");

    assert.deepEqual(
        { major: Number(match[1]), minor: Number(match[2]) },
        { major: CARBIDE_ROSLYN_VERSION.major, minor: CARBIDE_ROSLYN_VERSION.minor },
        "Carbide.Core's Roslyn version changed; update CARBIDE_ROSLYN_VERSION in " +
            "packages/nuget/src/analyzer-assets.ts so analyzer assets are selected for the " +
            "compiler that will actually run them.",
    );
});
