// Shared CLI ↔ @carbide/nuget option plumbing. Keeps each command's arg spec and extraction
// in one place so build / run / validate stay in sync.
//
// CLI flags:
//   --offline                   Forbid network access. Requires cached bytes or a replayed lock.
//   --lock <path>               Override the lock file path. Default: <projectDir>/carbide.lock.json.
//   --no-lock-write             Skip writing the lock file after a fresh resolve.
//   --nuget-source <url>        Override the flat-container base URL. Default: api.nuget.org/v3-flatcontainer/.
//   --allow-list-mode <mode>    strict | advisory | off. Default: strict.

import type { ParsedArgs } from "./args.js";
import { lastString } from "./args.js";

export const NUGET_STRING_FLAGS = ["lock", "nuget-source", "allow-list-mode"] as const;
export const NUGET_BOOLEAN_FLAGS = ["offline", "no-lock-write"] as const;

export type AllowListMode = "strict" | "advisory" | "off";

export interface NugetCliOptions {
    offline: boolean;
    noLockWrite: boolean;
    lockPath: string | undefined;
    nugetSource: string | undefined;
    allowListMode: AllowListMode;
}

export function extractNugetOptions(args: ParsedArgs, command: string): NugetCliOptions {
    const mode = lastString(args, "allow-list-mode") ?? "strict";
    if (mode !== "strict" && mode !== "advisory" && mode !== "off") {
        throw new Error(
            `carbide ${command}: --allow-list-mode must be one of strict|advisory|off (got '${mode}').`,
        );
    }
    return {
        offline: args.flags.has("offline"),
        noLockWrite: args.flags.has("no-lock-write"),
        lockPath: lastString(args, "lock"),
        nugetSource: lastString(args, "nuget-source"),
        allowListMode: mode,
    };
}
