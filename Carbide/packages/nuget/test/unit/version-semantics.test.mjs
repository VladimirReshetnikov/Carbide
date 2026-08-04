// NuGet version semantics that `@carbide/nuget` must match exactly, because getting them
// wrong silently resolves the wrong package graph rather than failing.
//
// Reference behaviour is NuGet's own `VersionComparer`: release labels compare with
// `StringComparer.OrdinalIgnoreCase`, and SemVer 2 build metadata is ignored for precedence.

import { test } from "node:test";
import assert from "node:assert/strict";
import { parseVersion, compareVersion, versionEq } from "../../dist/version-range.js";

const cmp = (a, b) => compareVersion(parseVersion(a), parseVersion(b));

test("pre-release labels compare case-insensitively", () => {
    // NuGet treats these as one version, not two. Reporting them as distinct would let the
    // resolver carry duplicate entries and emit spurious same-depth tie warnings.
    assert.equal(cmp("1.0.0-alpha", "1.0.0-ALPHA"), 0);
    assert.equal(cmp("1.0.0-Beta.2", "1.0.0-beta.2"), 0);
    assert.equal(versionEq(parseVersion("1.0.0-RC"), parseVersion("1.0.0-rc")), true);
});

test("pre-release ordering is ordinal, not locale-collated", () => {
    // `localeCompare` would make these depend on the host's locale data, so the same inputs
    // could resolve differently on different machines and break lock-file reproducibility.
    assert.ok(cmp("1.0.0-a", "1.0.0-b") < 0);
    assert.ok(cmp("1.0.0-B", "1.0.0-a") > 0, "ordinal-ignore-case: b > a regardless of case");
    // Non-ASCII is compared by code unit; the point is only that it is deterministic.
    assert.equal(
        Math.sign(cmp("1.0.0-z", "1.0.0-ä")),
        Math.sign("z".charCodeAt(0) - "ä".charCodeAt(0)),
    );
});

test("SemVer precedence rules hold for pre-release identifiers", () => {
    assert.ok(cmp("1.0.0-alpha", "1.0.0") < 0, "a pre-release sorts below its release");
    assert.ok(cmp("1.0.0-alpha", "1.0.0-alpha.1") < 0, "fewer identifiers sorts lower");
    assert.ok(cmp("1.0.0-alpha.1", "1.0.0-alpha.beta") < 0, "numeric sorts below alphanumeric");
    assert.ok(cmp("1.0.0-beta.2", "1.0.0-beta.11") < 0, "numeric identifiers compare numerically");
    assert.ok(cmp("1.0.0-rc.1", "1.0.0") < 0);
});

test("build metadata parses and is ignored for precedence", () => {
    const withMetadata = parseVersion("1.0.0+build.5");
    assert.equal(withMetadata.buildMetadata, "build.5");
    assert.equal(withMetadata.preRelease, "");
    assert.equal(withMetadata.raw, "1.0.0+build.5", "raw round-trips");

    assert.equal(cmp("1.0.0+build", "1.0.0"), 0);
    assert.equal(cmp("1.0.0-beta+exp.sha.5114f85", "1.0.0-beta"), 0);
    assert.equal(cmp("1.0.0+a", "1.0.0+b"), 0, "metadata never breaks a tie");
});

test("metadata containing a hyphen does not leak into the pre-release label", () => {
    // Splitting on `-` before removing `+metadata` would capture "beta+exp-1" as the label
    // and make this version compare as greater than plain "1.0.0-beta".
    const v = parseVersion("1.0.0-beta+exp-1");
    assert.equal(v.preRelease, "beta");
    assert.equal(v.buildMetadata, "exp-1");
    assert.equal(cmp("1.0.0-beta+exp-1", "1.0.0-beta"), 0);
});

test("a metadata-only version still parses its core correctly", () => {
    const v = parseVersion("2.3.4.5+meta");
    assert.deepEqual(
        [v.major, v.minor, v.patch, v.revision],
        [2, 3, 4, 5],
        "the `+` must not be read as part of the revision component",
    );
});

test("numeric components compare numerically, not lexically", () => {
    assert.ok(cmp("10.0.0", "2.0.0") > 0);
    assert.ok(cmp("1.0.10", "1.0.9") > 0);
    assert.ok(cmp("1.10.0", "1.9.0") > 0);
});

test("omitted components default to zero", () => {
    assert.equal(cmp("1.0.0", "1.0.0.0"), 0);
    assert.equal(cmp("1.0", "1.0.0.0"), 0);
    assert.equal(cmp("1", "1.0.0.0"), 0);
});
