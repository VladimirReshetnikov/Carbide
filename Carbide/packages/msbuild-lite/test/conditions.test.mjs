import { test } from "node:test";
import assert from "node:assert/strict";
import { evalCondition } from "../dist/conditions.js";

test("empty condition applies and evaluates", () => {
    const r = evalCondition(null, {});
    assert.deepEqual(r, { applies: true, evaluated: true });
});

test("simple equality with substitution", () => {
    const props = { configuration: "Release" };
    const r = evalCondition(" '$(Configuration)' == 'Release' ", props);
    assert.deepEqual(r, { applies: true, evaluated: true });
});

test("simple inequality", () => {
    const props = { configuration: "Debug" };
    const r = evalCondition(" '$(Configuration)' != 'Release' ", props);
    assert.deepEqual(r, { applies: true, evaluated: true });
});

test("and of two equalities", () => {
    const props = { configuration: "Release", platform: "AnyCPU" };
    const r = evalCondition(" '$(Configuration)' == 'Release' and '$(Platform)' == 'AnyCPU' ", props);
    assert.deepEqual(r, { applies: true, evaluated: true });
});

test("or where one branch applies", () => {
    const props = { configuration: "Debug" };
    const r = evalCondition(" '$(Configuration)' == 'Release' or '$(Configuration)' == 'Debug' ", props);
    assert.deepEqual(r, { applies: true, evaluated: true });
});

test("unparseable condition: applies true, evaluated false", () => {
    const r = evalCondition("Exists('foo.cs')", {});
    assert.equal(r.evaluated, false);
    assert.equal(r.applies, true);
});

test("missing property substitutes to empty string", () => {
    const r = evalCondition(" '$(Missing)' == '' ", {});
    assert.deepEqual(r, { applies: true, evaluated: true });
});
