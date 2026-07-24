// Builds test/fixtures/helper-dll/Helper.csproj and copies MyHelper.dll into this directory.
// Run once before tests that reference the helper DLL (wired from npm run build:test-fixtures).

import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { existsSync, copyFileSync } from "node:fs";
import path from "node:path";

const HERE = path.dirname(fileURLToPath(import.meta.url));

function run(cmd, args, cwd) {
    // No `shell: true`: dotnet is a real executable on every platform (not a .cmd shim),
    // and shell mode concatenates args unescaped (Node DEP0190).
    const result = spawnSync(cmd, args, { cwd, stdio: "inherit" });
    if (result.error) {
        throw result.error;
    }
    if (result.status !== 0) {
        throw new Error(`${cmd} ${args.join(" ")} exited with ${result.status}`);
    }
}

console.log(`[helper-dll] building in ${HERE}`);
run("dotnet", ["build", "Helper.csproj", "-c", "Release", "--nologo"], HERE);

const built = path.join(HERE, "bin", "Release", "netstandard2.0", "MyHelper.dll");
if (!existsSync(built)) {
    throw new Error(`Expected build output at ${built}`);
}
const dest = path.join(HERE, "MyHelper.dll");
copyFileSync(built, dest);
console.log(`[helper-dll] copied to ${dest}`);
