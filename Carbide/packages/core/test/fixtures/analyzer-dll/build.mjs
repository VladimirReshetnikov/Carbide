// Builds test/fixtures/analyzer-dll/Analyzer.csproj and copies CarbideTestAnalyzer.dll into
// this directory. Run once before tests that register a diagnostic analyzer (wired from
// npm run build:test-fixtures).

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

console.log(`[analyzer-dll] building in ${HERE}`);
run("dotnet", ["build", "Analyzer.csproj", "-c", "Release", "--nologo"], HERE);

const built = path.join(HERE, "bin", "Release", "netstandard2.0", "CarbideTestAnalyzer.dll");
if (!existsSync(built)) {
    throw new Error(`Expected build output at ${built}`);
}
const dest = path.join(HERE, "CarbideTestAnalyzer.dll");
copyFileSync(built, dest);
console.log(`[analyzer-dll] copied to ${dest}`);
