import { test } from "node:test";
import assert from "node:assert/strict";
import {
    parseVersion,
    parseRange,
    compareVersion,
    contains,
    bestMatch,
    VersionParseError,
} from "../../dist/version-range.js";

test("parseVersion: basic three-part", () => {
    const v = parseVersion("1.2.3");
    assert.deepEqual({ major: v.major, minor: v.minor, patch: v.patch, revision: v.revision }, {
        major: 1, minor: 2, patch: 3, revision: 0,
    });
    assert.equal(v.preRelease, "");
    assert.equal(v.raw, "1.2.3");
});

test("parseVersion: four-part revision", () => {
    const v = parseVersion("1.2.3.4");
    assert.equal(v.revision, 4);
});

test("parseVersion: pre-release identity", () => {
    const v = parseVersion("1.0.0-preview.2");
    assert.equal(v.preRelease, "preview.2");
});

test("parseVersion: floating versions rejected (MSNUGET001)", () => {
    assert.throws(() => parseVersion("1.*"), (err) => err instanceof VersionParseError && err.code === "MSNUGET001");
    assert.throws(() => parseVersion("*"), (err) => err instanceof VersionParseError && err.code === "MSNUGET001");
});

test("compareVersion: numeric ordering", () => {
    assert.ok(compareVersion(parseVersion("1.2.3"), parseVersion("1.2.4")) < 0);
    assert.ok(compareVersion(parseVersion("2.0.0"), parseVersion("1.99.99")) > 0);
    assert.equal(compareVersion(parseVersion("1.0.0"), parseVersion("1.0.0")), 0);
});

test("compareVersion: pre-release sorts below release", () => {
    assert.ok(compareVersion(parseVersion("1.0.0-alpha"), parseVersion("1.0.0")) < 0);
    assert.ok(compareVersion(parseVersion("1.0.0-alpha"), parseVersion("1.0.0-beta")) < 0);
});

test("parseRange: bare version ≥ X", () => {
    const r = parseRange("1.2.3");
    assert.equal(r.lower.raw, "1.2.3");
    assert.equal(r.lowerInclusive, true);
    assert.equal(r.upper, null);
});

test("parseRange: bracketed range", () => {
    const r = parseRange("[1.0.0,2.0.0)");
    assert.equal(r.lowerInclusive, true);
    assert.equal(r.upperInclusive, false);
    assert.equal(r.lower.raw, "1.0.0");
    assert.equal(r.upper.raw, "2.0.0");
});

test("parseRange: exact-pin via [x,x]", () => {
    const r = parseRange("[1.2.3]");
    assert.equal(r.lower.raw, "1.2.3");
    assert.equal(r.upper.raw, "1.2.3");
    assert.ok(r.lowerInclusive && r.upperInclusive);
});

test("parseRange: open upper (1.0.0,)", () => {
    const r = parseRange("(1.0.0,)");
    assert.equal(r.lower.raw, "1.0.0");
    assert.equal(r.lowerInclusive, false);
    assert.equal(r.upper, null);
});

test("contains: inclusive/exclusive edges", () => {
    const r = parseRange("[1.0.0,2.0.0)");
    assert.equal(contains(r, parseVersion("1.0.0")), true);
    assert.equal(contains(r, parseVersion("1.9.9")), true);
    assert.equal(contains(r, parseVersion("2.0.0")), false);
    assert.equal(contains(r, parseVersion("0.9.9")), false);
});

test("bestMatch: lowest satisfying version", () => {
    const r = parseRange("[1.0.0,)");
    const avail = ["0.9.0", "1.0.0", "1.5.0", "2.0.0"].map(parseVersion);
    const match = bestMatch(r, avail);
    assert.equal(match.raw, "1.0.0");
});

test("bestMatch: null when nothing satisfies", () => {
    const r = parseRange("[5.0.0,)");
    const avail = ["1.0.0", "2.0.0"].map(parseVersion);
    assert.equal(bestMatch(r, avail), null);
});
