// @carbide-ui/refs-avalonia build script — UI-M0 stub.
//
// At UI-M1 this script will:
//   1. Download the pinned Avalonia.Browser.<version>.nupkg plus its transitive refs
//      from api.nuget.org/v3-flatcontainer/...
//   2. Extract ref/<tfm>/*.dll entries (filtering out analyzers/, build/, lib/, tools/
//      and runtimes/ — see UI-I8).
//   3. Write ref/<tfm>/*.dll and refpack.json with per-DLL size + SHA256.
// See carbide-ui-avalonia-approach-b-plan §5 for the full shape.

console.log("[refs-avalonia] UI-M0 stub. UI-M1 will implement this.");
