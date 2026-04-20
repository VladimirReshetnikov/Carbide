// Writes the Microsoft.NET ECMA-style open public key (token b03f5f7f11d50a3a) used
// across the .NET BCL to `microsoft-net-public-key.snk` in this directory. Run this once
// with `node write-pk.mjs`; the result is committed alongside the csproj so the fork's
// AssemblyRef identity matches the stock System.Console.dll's (same simple name, same
// version, same PublicKeyToken). Mono-WASM browser does not verify strong-name hashes,
// so PublicSign-mode signing (public key only, no private-key signature) is sufficient.
import { writeFileSync } from "node:fs";
const hex =
    "002400000480000094000000060200000024000052534131000400000100010007D1FA57C4AED9F0" +
    "A32E84AA0FAEFD0DE9E8FD6AEC8F87FB03766C834C99921EB23BE79AD9D5DCC1DD9AD23613210290" +
    "0B723CF980957FC4E177108FC607774F29E8320E92EA05ECE4E821C0A5EFE8F1645C4C0C93C1AB99" +
    "285D622CAA652C1DFAD63D745D6F2DE5F17E5EAF0FC4963D261C8A12436518206DC093344D5AD293";
const bytes = Buffer.from(hex, "hex");
writeFileSync(new URL("./microsoft-net-public-key.snk", import.meta.url), bytes);
console.log(`wrote ${bytes.length} bytes`);
