import { test } from "node:test";
import assert from "node:assert/strict";
import { checkSafety } from "../../dist/safety.js";
import { MSNUGET_CODES } from "../../dist/warnings.js";

test("checkSafety: clean lib-only package is ok", () => {
    const result = checkSafety("Newtonsoft.Json", "13.0.3", [
        "Newtonsoft.Json.nuspec",
        "lib/net10.0/Newtonsoft.Json.dll",
        "lib/netstandard2.0/Newtonsoft.Json.dll",
    ]);
    assert.equal(result.kind, "ok");
});

test("checkSafety: refuses native binaries under runtimes/<rid>/native/", () => {
    const result = checkSafety("Pkg", "1.0.0", [
        "lib/net10.0/Pkg.dll",
        "runtimes/win-x64/native/pkg_native.dll",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_NATIVE);
    assert.equal(result.offendingEntry, "runtimes/win-x64/native/pkg_native.dll");
    assert.match(result.message, /Mono-WASM runtime cannot load/);
});

test("checkSafety: refuses MSBuild build/<id>.targets", () => {
    const result = checkSafety("Pkg", "1.0.0", [
        "lib/net10.0/Pkg.dll",
        "build/Pkg.targets",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_TARGETS);
    assert.match(result.message, /does not execute MSBuild tasks/);
});

test("checkSafety: refuses MSBuild buildTransitive/<id>.props", () => {
    const result = checkSafety("Pkg", "1.0.0", [
        "lib/net10.0/Pkg.dll",
        "buildTransitive/Pkg.props",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_TARGETS);
});

test("checkSafety: refuses analyzers/", () => {
    const result = checkSafety("Pkg", "1.0.0", [
        "lib/net10.0/Pkg.dll",
        "analyzers/dotnet/cs/Pkg.Analyzers.dll",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_ANALYZERS);
    assert.match(result.message, /Roslyn analyzer/);
});

test("checkSafety: normalises backslashes before matching", () => {
    // NuGet convention is forward slashes but some older .nupkgs ship Windows paths.
    const result = checkSafety("Pkg", "1.0.0", [
        "runtimes\\win-x64\\native\\pkg_native.dll",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_NATIVE);
});

test("checkSafety: is case-insensitive", () => {
    const result = checkSafety("Pkg", "1.0.0", [
        "BUILD/Pkg.TARGETS",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_TARGETS);
});

test("checkSafety: benign build/ entries that aren't .targets/.props pass", () => {
    // Not every build/ entry is hostile — some packages ship readmes or icons there.
    const result = checkSafety("Pkg", "1.0.0", [
        "lib/net10.0/Pkg.dll",
        "build/README.md",
    ]);
    assert.equal(result.kind, "ok");
});

test("checkSafety: stops at first offender (native beats analyzer in iteration order)", () => {
    const result = checkSafety("Pkg", "1.0.0", [
        "runtimes/win-x64/native/a.dll",
        "analyzers/dotnet/cs/b.dll",
    ]);
    assert.equal(result.kind, "refused");
    assert.equal(result.code, MSNUGET_CODES.SAFETY_NATIVE);
});
