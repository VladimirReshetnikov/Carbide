// Hand-rolled arg parser for the carbide CLI. See M4 plan §5 D41.
// Supports:
//   - Named flags: --key value (or --key=value).
//   - Boolean flags: --flag (no value).
//   - Repeatable flags: values accumulate into a string[].
//   - Program-argument separator: anything after a lone "--" goes into `programArgs`.
// Does not support single-letter short flags. Keep it minimal.

export interface ParsedArgs {
    command: string | undefined;
    named: Map<string, string[]>;
    flags: Set<string>;
    positional: string[];
    programArgs: string[];
}

export class ArgParseError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "ArgParseError";
    }
}

/**
 * Parses a command-line argv (without the leading `node carbide` bits).
 *
 * Syntax:
 *   carbide <command> [--key value]... [--flag]... [--] [<program args>]
 *
 * The caller declares which named options are string-valued (repeatable) and which are
 * boolean flags. Anything unknown throws.
 */
export interface ArgSpec {
    /** Named options that take a string value (repeatable). */
    readonly strings: readonly string[];
    /** Boolean flags (no value). */
    readonly booleans: readonly string[];
    /** Allow positional arguments before `--`. Default false. */
    readonly allowPositional?: boolean;
}

export function parseArgs(argv: readonly string[], spec: ArgSpec): ParsedArgs {
    const named = new Map<string, string[]>();
    const flags = new Set<string>();
    const positional: string[] = [];
    const programArgs: string[] = [];
    const stringOptions = new Set(spec.strings);
    const booleanOptions = new Set(spec.booleans);

    let command: string | undefined;
    let hitDoubleDash = false;

    for (let i = 0; i < argv.length; i++) {
        const token = argv[i];
        if (hitDoubleDash) {
            programArgs.push(token);
            continue;
        }
        if (token === "--") {
            hitDoubleDash = true;
            continue;
        }
        if (!token.startsWith("--")) {
            if (command === undefined) {
                command = token;
                continue;
            }
            if (!spec.allowPositional) {
                throw new ArgParseError(`Unexpected positional argument '${token}'.`);
            }
            positional.push(token);
            continue;
        }

        // Named flag or option.
        const eq = token.indexOf("=");
        const name = eq >= 0 ? token.slice(2, eq) : token.slice(2);
        const inlineValue = eq >= 0 ? token.slice(eq + 1) : undefined;

        if (booleanOptions.has(name)) {
            if (inlineValue !== undefined) {
                throw new ArgParseError(`--${name} is a boolean flag and does not accept a value.`);
            }
            flags.add(name);
            continue;
        }
        if (stringOptions.has(name)) {
            let value: string | undefined;
            if (inlineValue !== undefined) {
                value = inlineValue;
            } else if (i + 1 < argv.length) {
                value = argv[++i];
            }
            if (value === undefined) {
                throw new ArgParseError(`--${name} requires a value.`);
            }
            const existing = named.get(name);
            if (existing) {
                existing.push(value);
            } else {
                named.set(name, [value]);
            }
            continue;
        }
        throw new ArgParseError(`Unknown option '--${name}'.`);
    }

    return { command, named, flags, positional, programArgs };
}

/** Convenience: fetch a string[] value defaulting to [] when the option wasn't passed. */
export function stringList(args: ParsedArgs, name: string): string[] {
    return args.named.get(name) ?? [];
}

/** Convenience: fetch the last value of a single-value option (or a default). */
export function lastString(args: ParsedArgs, name: string, defaultValue: string | undefined = undefined): string | undefined {
    const list = args.named.get(name);
    return list && list.length > 0 ? list[list.length - 1] : defaultValue;
}
