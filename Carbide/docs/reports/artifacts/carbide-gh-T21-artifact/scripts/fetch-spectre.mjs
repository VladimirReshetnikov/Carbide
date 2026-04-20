// Vendors Spectre.Console.dll into `public/lib/` so the browser demo can fetch it at
// runtime and hand it to `session.addReference(bytes)`.
//
// Approach: use the local .NET SDK to restore Spectre.Console into a throwaway csproj,
// then copy the restored DLL out of the NuGet global cache. Avoids a zip-parser dep and
// matches how any .NET SDK machine already has Spectre cached (usually zero network).
import { mkdirSync, readFileSync, writeFileSync, existsSync, copyFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import os from "node:os";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoDemoDir = resolve(__dirname, "..");
const libDir = join(repoDemoDir, "public", "lib");
const fetcherDir = join(os.tmpdir(), "carbide-gh-fetch");

const SPECTRE_VERSION = process.env.SPECTRE_VERSION ?? "0.49.1";

function run(cmd, args, opts = {}) {
    console.log(`$ ${cmd} ${args.join(" ")}`);
    return execFileSync(cmd, args, { stdio: "inherit", ...opts });
}

function main() {
    mkdirSync(libDir, { recursive: true });
    mkdirSync(fetcherDir, { recursive: true });

    const csproj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Spectre.Console" Version="${SPECTRE_VERSION}" />
  </ItemGroup>
</Project>`;
    writeFileSync(join(fetcherDir, "fetcher.csproj"), csproj);
    // A dummy Program.cs keeps `dotnet restore`+`build` happy. Build (not just restore)
    // copies Spectre.Console + its real transitive deps into bin/net8.0, which is easier
    // to scoop than walking every ~/.nuget/packages/*/lib/net8.0/ entry by hand.
    writeFileSync(join(fetcherDir, "Program.cs"), "namespace F { internal static class Dummy { public static void Main() { } } }\n");

    run("dotnet", ["build", "-c", "Release", "--nologo", "-v", "q", fetcherDir]);

    const binNet8 = join(fetcherDir, "bin", "Release", "net8.0");
    // Copy every DLL that ended up in bin/ that isn't part of the .NET runtime itself —
    // i.e. the package-supplied assemblies we want the browser to load as references.
    const wantedDlls = [
        "Spectre.Console.dll",
    ];
    // Spectre has no external NuGet deps at 0.49.x (verified by inspecting the nuspec);
    // the above list is exhaustive today. If Spectre gains deps in a future bump, add
    // them here. We deliberately do NOT copy fetcher.dll, System.*, Microsoft.*, etc. —
    // those are either stock BCL (served from Carbide's `_framework/`) or runtime-only.

    for (const dll of wantedDlls) {
        const src = join(binNet8, dll);
        if (!existsSync(src)) {
            throw new Error(`expected ${dll} in ${binNet8} after build — did the NuGet restore succeed?`);
        }
        const dest = join(libDir, dll);
        copyFileSync(src, dest);
        const size = readFileSync(dest).length;
        console.log(`  \u2713 ${dll} \u2192 public/lib/${dll} (${size.toLocaleString()} bytes)`);
    }

    console.log(`\nDone. public/lib/ now contains ${wantedDlls.length} DLL(s) vendored from Spectre.Console ${SPECTRE_VERSION}.`);
}

main();
