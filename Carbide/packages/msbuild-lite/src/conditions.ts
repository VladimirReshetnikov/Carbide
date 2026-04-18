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

    // Handle 'and' / 'or' at the top level. This is a shallow split — no nested parens — which
    // matches cs_kit's behaviour.
    for (const op of ["and", "or"] as const) {
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

    // '<LHS>' ==|!= '<RHS>'
    const m = normalised.match(/^(.+?)\s*(==|!=)\s*(.+)$/);
    if (!m) {
        return { applies: true, evaluated: false };
    }
    const left = stripOuterSingleQuotes(substituteVars(m[1], properties));
    const right = stripOuterSingleQuotes(substituteVars(m[3], properties));
    const result = m[2] === "==" ? left === right : left !== right;
    return { applies: result, evaluated: true };
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
 */
function splitTopLevel(expr: string, op: "and" | "or"): string[] {
    const re = new RegExp(`\\s+${op}\\s+`, "i");
    // Use a simple split — no nested parens handling; cs_kit mirrors this.
    const parts = expr.split(re);
    return parts;
}
