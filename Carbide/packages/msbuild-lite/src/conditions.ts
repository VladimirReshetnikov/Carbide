// MSBuild Condition evaluator — simple subset only. Mirrors cs_kit.msbuild_lite._eval_condition.
//
// Supported:
//   '$(X)' == 'Y'
//   '$(X)' != 'Y'
//   <expr> and <expr>    (simple conjunction)
//   <expr> or <expr>     (simple disjunction)
//
// Anything else (property functions, Exists(), item references) returns
// { applies: true, evaluated: false } so the element stays in scope and a warning can fire.

export interface CondResult {
    applies: boolean;
    evaluated: boolean;
}

export function evalCondition(
    condition: string | null | undefined,
    properties: Record<string, string>,
): CondResult {
    if (!condition || !condition.trim()) {
        return { applies: true, evaluated: true };
    }

    const normalised = condition.trim().replace(/\s+/g, " ");

    // Handle 'and' / 'or' at the top level. This is a shallow split — no nested parens.
    //
    // `or` must be split FIRST. MSBuild gives `and` the higher precedence, so `A or B and C`
    // means `A or (B and C)`; splitting on `and` first would group it as `(A or B) and C` and
    // return a confidently wrong answer. Splitting on the loosest-binding operator first and
    // recursing gives the correct tree.
    for (const op of ["or", "and"] as const) {
        const parts = splitTopLevel(normalised, op);
        if (parts.length > 1) {
            const results: boolean[] = [];
            for (const part of parts) {
                const r = evalCondition(part, properties);
                if (!r.evaluated) {
                    return { applies: true, evaluated: false };
                }
                results.push(r.applies);
            }
            const applies = op === "and" ? results.every((b) => b) : results.some((b) => b);
            return { applies, evaluated: true };
        }
    }

    // Leading `!` negation. MSBuild binds `!` tighter than `==`, so only a parenthesised
    // group has an unambiguous meaning in this subset; other `!` forms are refused rather
    // than folded into the left operand. Doing the latter is what used to happen, turning
    // `!('$(Configuration)' == 'release')` into a comparison of the literal `!('debug'`
    // against `'release')` — false, where MSBuild says true.
    if (normalised.startsWith("!")) {
        const inner = unwrapParenthesisedGroup(normalised.slice(1).trim());
        if (inner === null) {
            return { applies: true, evaluated: false };
        }
        const negated = evalCondition(inner, properties);
        return negated.evaluated
            ? { applies: !negated.applies, evaluated: true }
            : { applies: true, evaluated: false };
    }

    // A redundant group around a whole expression, e.g. `('$(X)' == 'Y')`.
    const grouped = unwrapParenthesisedGroup(normalised);
    if (grouped !== null) {
        return evalCondition(grouped, properties);
    }

    // '<LHS>' ==|!= '<RHS>'
    const m = normalised.match(/^(.+?)\s*(==|!=)\s*(.+)$/);
    if (!m) {
        return { applies: true, evaluated: false };
    }
    const left = stripOuterSingleQuotes(substituteVars(m[1], properties));
    const right = stripOuterSingleQuotes(substituteVars(m[3], properties));
    // MSBuild compares strings case-insensitively, so `'$(Configuration)' == 'Debug'` matches
    // a `Configuration` of `debug`. Comparing case-sensitively silently skipped whole
    // `<PropertyGroup>`s whose condition a real build would have taken. JS `toLowerCase` is
    // locale-independent (unlike `toLocaleLowerCase`), so this stays deterministic.
    const equal = left.toLowerCase() === right.toLowerCase();
    return { applies: m[2] === "==" ? equal : !equal, evaluated: true };
}

/**
 * Substitute `$(Name)` references against the property bag. Case-insensitive key lookup
 * (cs_kit uses lowercased keys); missing properties substitute to the empty string.
 */
export function substituteVars(expr: string, properties: Record<string, string>): string {
    return expr.replace(/\$\(([^)]+)\)/g, (_, name: string) => {
        const key = name.trim().toLowerCase();
        return properties[key] ?? "";
    });
}

/** Strip a single pair of surrounding single quotes, if present. */
export function stripOuterSingleQuotes(expr: string): string {
    const trimmed = expr.trim();
    if (trimmed.length >= 2 && trimmed.startsWith("'") && trimmed.endsWith("'")) {
        return trimmed.slice(1, -1);
    }
    return trimmed;
}

/**
 * Split an expression on the top-level operator (case-insensitive, whole-word match). Does
 * not descend into parentheses. Returns [expr] if the operator isn't found.
 *
 * Occurrences inside single-quoted literals are skipped: `'$(X)' == 'a and b'` is one
 * comparison, not a conjunction. A plain `String.split` tore that into fragments, and while
 * the fragments happened to fail parsing — degrading to "unevaluated" rather than to a wrong
 * answer — the condition was needlessly lost.
 */
function splitTopLevel(expr: string, op: "and" | "or"): string[] {
    const parts: string[] = [];
    let segmentStart = 0;
    let inQuotes = false;
    let depth = 0;

    for (let i = 0; i < expr.length; i++) {
        if (expr[i] === "'") {
            inQuotes = !inQuotes;
            continue;
        }
        if (!inQuotes && (expr[i] === "(" || expr[i] === ")")) {
            depth += expr[i] === "(" ? 1 : -1;
            continue;
        }
        if (inQuotes || depth > 0 || expr[i] !== " ") {
            continue;
        }
        // At a space outside quotes: does the operator start here, as a whole word?
        const candidate = expr.slice(i + 1, i + 1 + op.length);
        if (candidate.toLowerCase() !== op || expr[i + 1 + op.length] !== " ") {
            continue;
        }
        parts.push(expr.slice(segmentStart, i));
        i += op.length + 1;
        segmentStart = i + 1;
    }

    parts.push(expr.slice(segmentStart));
    return parts;
}

/**
 * If `expr` is entirely wrapped in one balanced pair of parentheses, return its contents;
 * otherwise `null`. Quote-aware, so `('a)b')` is not mistaken for an unbalanced group, and
 * depth-aware, so `(a) and (b)` is correctly reported as *not* a single group.
 */
function unwrapParenthesisedGroup(expr: string): string | null {
    if (!expr.startsWith("(") || !expr.endsWith(")")) {
        return null;
    }
    let depth = 0;
    let inQuotes = false;
    for (let i = 0; i < expr.length; i++) {
        if (expr[i] === "'") {
            inQuotes = !inQuotes;
            continue;
        }
        if (inQuotes) {
            continue;
        }
        if (expr[i] === "(") {
            depth++;
        } else if (expr[i] === ")") {
            depth--;
            // The opening paren closed before the end, so the group is not the whole
            // expression — e.g. `(a) and (b)`.
            if (depth === 0 && i !== expr.length - 1) {
                return null;
            }
        }
    }
    return depth === 0 ? expr.slice(1, -1).trim() : null;
}
