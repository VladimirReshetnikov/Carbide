// M12 — analyzer asset selection, following NuGet's
// `analyzers/dotnet/[roslyn<X.Y>/][<lang>/]` convention.

import { test } from "node:test";
import assert from "node:assert/strict";
import { selectAnalyzerAssets, CARBIDE_ROSLYN_VERSION } from "../../dist/index.js";

const HOST = { major: 4, minor: 14 };

test("selects the unversioned cs layout", () => {
    const result = selectAnalyzerAssets(
        ["lib/net10.0/Pkg.dll", "analyzers/dotnet/cs/Pkg.Generators.dll"],
        HOST,
    );
    assert.deepEqual(result.entries, ["analyzers/dotnet/cs/Pkg.Generators.dll"]);
    assert.deepEqual(result.unrecognised, []);
});

test("selects a language-agnostic asset directly under dotnet/", () => {
    const result = selectAnalyzerAssets(["analyzers/dotnet/Pkg.Generators.dll"], HOST);
    assert.deepEqual(result.entries, ["analyzers/dotnet/Pkg.Generators.dll"]);
});

test("picks the highest roslyn folder this host can load, and only that one", () => {
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/roslyn3.11/cs/Pkg.Generators.dll",
            "analyzers/dotnet/roslyn4.4/cs/Pkg.Generators.dll",
            "analyzers/dotnet/roslyn4.8/cs/Pkg.Generators.dll",
        ],
        HOST,
    );
    // Loading more than one would run the same generator several times.
    assert.deepEqual(result.entries, ["analyzers/dotnet/roslyn4.8/cs/Pkg.Generators.dll"]);
    assert.deepEqual(result.unrecognised, []);
});

test("ignores roslyn folders newer than the host", () => {
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/roslyn4.4/cs/Pkg.Generators.dll",
            "analyzers/dotnet/roslyn9.0/cs/Pkg.Generators.dll",
        ],
        HOST,
    );
    assert.deepEqual(result.entries, ["analyzers/dotnet/roslyn4.4/cs/Pkg.Generators.dll"]);
});

test("a roslyn-versioned folder wins over the unversioned fallback", () => {
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/cs/Pkg.Generators.dll",
            "analyzers/dotnet/roslyn4.4/cs/Pkg.Generators.dll",
        ],
        HOST,
    );
    assert.deepEqual(result.entries, ["analyzers/dotnet/roslyn4.4/cs/Pkg.Generators.dll"]);
});

test("a package shipping only newer roslyn folders reports them rather than looking empty", () => {
    // The dangerous outcome here is a silent empty selection: the caller would read it as
    // "this package has no analyzers" when in fact it has one this host cannot load.
    const result = selectAnalyzerAssets(["analyzers/dotnet/roslyn9.0/cs/Pkg.Generators.dll"], HOST);
    assert.deepEqual(result.entries, []);
    assert.deepEqual(result.unrecognised, ["analyzers/dotnet/roslyn9.0/cs/Pkg.Generators.dll"]);
});

test("VB and F# analyzers are skipped without comment", () => {
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/cs/Pkg.Generators.dll",
            "analyzers/dotnet/vb/Pkg.Generators.dll",
            "analyzers/dotnet/fs/Pkg.Generators.dll",
        ],
        HOST,
    );
    assert.deepEqual(result.entries, ["analyzers/dotnet/cs/Pkg.Generators.dll"]);
    // Not a gap — those assets were never ours to run.
    assert.deepEqual(result.unrecognised, []);
});

test("an unrecognised layout is reported, not guessed at", () => {
    const result = selectAnalyzerAssets(["analyzers/cs/Pkg.Generators.dll"], HOST);
    assert.deepEqual(result.entries, []);
    assert.deepEqual(result.unrecognised, ["analyzers/cs/Pkg.Generators.dll"]);
});

test("satellite resource assemblies are neither selected nor reported", () => {
    // Real Microsoft packages ship 13 of these per roslyn folder. Reporting them as
    // unplaceable analyzers produced 39 spurious MSNUGET017 entries for System.Text.Json
    // alone — enough noise to make the warning worthless. Found by running the selector
    // against real nupkgs; no hand-written fixture had the culture sub-folder.
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/roslyn4.4/cs/System.Text.Json.SourceGeneration.dll",
            "analyzers/dotnet/roslyn4.4/cs/de/System.Text.Json.SourceGeneration.resources.dll",
            "analyzers/dotnet/roslyn4.4/cs/zh-Hant/System.Text.Json.SourceGeneration.resources.dll",
            "analyzers/dotnet/roslyn3.11/cs/ja/System.Text.Json.SourceGeneration.resources.dll",
        ],
        HOST,
    );
    assert.deepEqual(result.entries, [
        "analyzers/dotnet/roslyn4.4/cs/System.Text.Json.SourceGeneration.dll",
    ]);
    assert.deepEqual(result.unrecognised, []);
});

test("a package shipping several analyzers in one folder selects all of them", () => {
    // CommunityToolkit.Mvvm's real layout: a code-fix assembly beside the generator. Both are
    // selected; the consumer discovers which one carries a generator by loading it.
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/roslyn4.0/cs/CommunityToolkit.Mvvm.CodeFixers.dll",
            "analyzers/dotnet/roslyn4.0/cs/CommunityToolkit.Mvvm.SourceGenerators.dll",
            "analyzers/dotnet/roslyn4.3/cs/CommunityToolkit.Mvvm.CodeFixers.dll",
            "analyzers/dotnet/roslyn4.3/cs/CommunityToolkit.Mvvm.SourceGenerators.dll",
        ],
        HOST,
    );
    assert.deepEqual(result.entries, [
        "analyzers/dotnet/roslyn4.3/cs/CommunityToolkit.Mvvm.CodeFixers.dll",
        "analyzers/dotnet/roslyn4.3/cs/CommunityToolkit.Mvvm.SourceGenerators.dll",
    ]);
    assert.deepEqual(result.unrecognised, []);
});

test("non-DLL entries beside an analyzer are neither selected nor reported", () => {
    // Reporting a .pdb or .xml as a missed analyzer would train callers to ignore MSNUGET017.
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/cs/Pkg.Generators.dll",
            "analyzers/dotnet/cs/Pkg.Generators.pdb",
            "analyzers/dotnet/cs/Pkg.Generators.xml",
            "analyzers/",
        ],
        HOST,
    );
    assert.deepEqual(result.entries, ["analyzers/dotnet/cs/Pkg.Generators.dll"]);
    assert.deepEqual(result.unrecognised, []);
});

test("backslash-separated entries are matched too", () => {
    const result = selectAnalyzerAssets(["analyzers\\dotnet\\cs\\Pkg.Generators.dll"], HOST);
    // The original entry string is returned, so the caller can look it up in the zip.
    assert.deepEqual(result.entries, ["analyzers\\dotnet\\cs\\Pkg.Generators.dll"]);
});

test("multiple assets in the chosen folder are all selected, in ordinal order", () => {
    const result = selectAnalyzerAssets(
        [
            "analyzers/dotnet/cs/Zebra.dll",
            "analyzers/dotnet/cs/Alpha.dll",
            "analyzers/dotnet/cs/alpha.dll",
        ],
        HOST,
    );
    // Ordinal, not locale: uppercase sorts before lowercase, and the order must be identical
    // on every machine.
    assert.deepEqual(result.entries, [
        "analyzers/dotnet/cs/Alpha.dll",
        "analyzers/dotnet/cs/Zebra.dll",
        "analyzers/dotnet/cs/alpha.dll",
    ]);
});

test("a package with no analyzers selects nothing and reports nothing", () => {
    const result = selectAnalyzerAssets(["lib/net10.0/Pkg.dll", "Pkg.nuspec"], HOST);
    assert.deepEqual(result.entries, []);
    assert.deepEqual(result.unrecognised, []);
});

test("the default host version is Carbide's own Roslyn version", () => {
    assert.deepEqual({ ...CARBIDE_ROSLYN_VERSION }, HOST);
    assert.deepEqual(
        selectAnalyzerAssets(["analyzers/dotnet/roslyn4.14/cs/Pkg.dll"]).entries,
        ["analyzers/dotnet/roslyn4.14/cs/Pkg.dll"],
    );
});

test("a bare roslyn<major> folder parses as <major>.0", () => {
    const result = selectAnalyzerAssets(
        ["analyzers/dotnet/roslyn4/cs/Pkg.dll", "analyzers/dotnet/roslyn4.4/cs/Pkg.dll"],
        HOST,
    );
    assert.deepEqual(result.entries, ["analyzers/dotnet/roslyn4.4/cs/Pkg.dll"]);
});
