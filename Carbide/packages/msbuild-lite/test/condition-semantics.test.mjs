// MSBuild `Condition` semantics that the evaluator must match.
//
// A condition that evaluates to the wrong answer with `evaluated: true` is the dangerous
// case: the element is confidently kept or dropped, and nothing warns. These tests pin the
// rules where a plain reading of the expression would diverge from MSBuild.

import { test } from "node:test";
import assert from "node:assert/strict";
import { evalCondition } from "../dist/conditions.js";

// Property bags are keyed lowercase; values keep their original casing.
const props = { configuration: "debug", targetframework: "net10.0", os: "Windows_NT" };
const evaluate = (condition) => evalCondition(condition, props);

test("string comparison is case-insensitive, as in MSBuild", () => {
    // A real build takes this PropertyGroup; comparing case-sensitively silently skipped it.
    assert.deepEqual(evaluate("'$(Configuration)' == 'Debug'"), { applies: true, evaluated: true });
    assert.deepEqual(evaluate("'$(TargetFramework)' == 'NET10.0'"), { applies: true, evaluated: true });
    assert.deepEqual(evaluate("'$(OS)' == 'windows_nt'"), { applies: true, evaluated: true });
});

test("inequality is case-insensitive too", () => {
    assert.deepEqual(evaluate("'$(Configuration)' != 'Debug'"), { applies: false, evaluated: true });
    assert.deepEqual(evaluate("'$(Configuration)' != 'Release'"), { applies: true, evaluated: true });
});

test("'and' binds tighter than 'or'", () => {
    // true or (false and false) === true.  Grouping as (true or false) and false gives false,
    // which is what a left-to-right split produced.
    const condition =
        "'$(Configuration)' == 'debug' or '$(Configuration)' == 'zzz' and '$(Configuration)' == 'yyy'";
    assert.deepEqual(evaluate(condition), { applies: true, evaluated: true });
});

test("'and' before 'or' groups correctly in the other order too", () => {
    // (false and true) or true === true.
    const condition =
        "'$(Configuration)' == 'zzz' and '$(Configuration)' == 'debug' or '$(OS)' == 'Windows_NT'";
    assert.deepEqual(evaluate(condition), { applies: true, evaluated: true });
});

test("plain conjunction and disjunction still work", () => {
    assert.deepEqual(
        evaluate("'$(Configuration)' == 'debug' and '$(OS)' == 'Windows_NT'"),
        { applies: true, evaluated: true },
    );
    assert.deepEqual(
        evaluate("'$(Configuration)' == 'zzz' and '$(OS)' == 'Windows_NT'"),
        { applies: false, evaluated: true },
    );
    assert.deepEqual(
        evaluate("'$(Configuration)' == 'zzz' or '$(OS)' == 'Windows_NT'"),
        { applies: true, evaluated: true },
    );
});

test("operator words inside a quoted literal do not split the expression", () => {
    // One comparison against the literal "a and b", not a conjunction.
    assert.deepEqual(evaluate("'$(Configuration)' == 'a and b'"), { applies: false, evaluated: true });
    assert.deepEqual(evaluate("'$(Configuration)' == 'x or y'"), { applies: false, evaluated: true });
    // And it still matches when the value genuinely contains the word.
    assert.deepEqual(
        evalCondition("'$(Description)' == 'fast and small'", { description: "fast and small" }),
        { applies: true, evaluated: true },
    );
});

test("a substring that merely starts with the operator is not an operator", () => {
    assert.deepEqual(
        evalCondition("'$(Mode)' == 'android'", { mode: "android" }),
        { applies: true, evaluated: true },
    );
});

test("negation of a parenthesised group inverts the result", () => {
    // Previously the `!` was folded into the left operand, so this compared the literal
    // `!('debug'` against `'release')` and answered false where MSBuild answers true.
    assert.deepEqual(
        evaluate("!('$(Configuration)' == 'release')"),
        { applies: true, evaluated: true },
    );
    assert.deepEqual(
        evaluate("!('$(Configuration)' == 'debug')"),
        { applies: false, evaluated: true },
    );
    assert.deepEqual(
        evaluate("!('$(Configuration)' == 'debug' and '$(OS)' == 'Windows_NT')"),
        { applies: false, evaluated: true },
    );
});

test("negation composes with the surrounding operators", () => {
    assert.deepEqual(
        evaluate("!('$(Configuration)' == 'zzz') and '$(OS)' == 'Windows_NT'"),
        { applies: true, evaluated: true },
    );
});

test("explicit parentheses override the default precedence", () => {
    // (false or true) and true === true. Without paren-aware splitting the group would be
    // torn apart and the condition lost.
    assert.deepEqual(
        evaluate("('$(Configuration)' == 'zzz' or '$(OS)' == 'Windows_NT') and '$(Configuration)' == 'debug'"),
        { applies: true, evaluated: true },
    );
    // A redundant group around a whole comparison is transparent.
    assert.deepEqual(evaluate("('$(Configuration)' == 'debug')"), { applies: true, evaluated: true });
});

test("unsupported constructs stay in scope and report themselves as unevaluated", () => {
    // The contract the caller relies on to emit a warning rather than silently dropping.
    assert.deepEqual(evaluate("Exists('foo.props')"), { applies: true, evaluated: false });
    assert.deepEqual(evaluate("'$(Version)' > '1.0'"), { applies: true, evaluated: false });
    // `!` on anything but a parenthesised group has no unambiguous meaning in this subset,
    // and must be refused rather than mis-parsed.
    assert.equal(evaluate("!Exists('foo.props')").evaluated, false);
    assert.equal(evaluate("!'$(Configuration)'").evaluated, false);
});

test("an empty or missing condition applies", () => {
    assert.deepEqual(evaluate(""), { applies: true, evaluated: true });
    assert.deepEqual(evaluate("   "), { applies: true, evaluated: true });
    assert.deepEqual(evaluate(null), { applies: true, evaluated: true });
    assert.deepEqual(evaluate(undefined), { applies: true, evaluated: true });
});

test("an unset property substitutes to empty string", () => {
    assert.deepEqual(evaluate("'$(NotDefined)' == ''"), { applies: true, evaluated: true });
    assert.deepEqual(evaluate("'$(NotDefined)' != ''"), { applies: false, evaluated: true });
});
