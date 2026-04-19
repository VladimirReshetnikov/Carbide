// MSBuild reserved/well-known properties that drive `<Import Project="$(…)"/>` path
// substitution and per-file context. See
// https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-reserved-and-well-known-properties.
//
// Subset we populate (M11 scope):
//   $(MSBuildThisFile)             — basename with extension, of the file being evaluated.
//   $(MSBuildThisFileName)         — basename without extension.
//   $(MSBuildThisFileDirectory)    — directory (OS-native sep), WITH trailing separator.
//   $(MSBuildThisFileFullPath)     — absolute path to the file being evaluated.
//   $(MSBuildProjectDirectory)     — directory (no trailing sep), of the root csproj.
//   $(MSBuildProjectFile)          — basename with extension, of the root csproj.
//   $(MSBuildProjectName)          — basename without extension, of the root csproj.
//   $(MSBuildProjectFullPath)      — absolute path to the root csproj.
//   $(MSBuildProjectExtension)     — extension including the leading dot (".csproj").
//
// Not implemented:
//   $(MSBuildExtensionsPath), $(MSBuildToolsPath), $(MSBuildSDKsPath), and friends — these
//   depend on an actual MSBuild installation which Carbide does not assume exists. Users
//   whose csproj trees reach for those paths get the empty string (no substitution), same
//   as any other unknown $(X) reference.

import path from "node:path";

/**
 * Compute the reserved property values for one evaluator frame. `currentFile` is the file
 * currently being walked (for the `$(MSBuildThisFile*)` family); `rootProjectPath` is the
 * top-level csproj (for the `$(MSBuildProject*)` family). Both are absolute paths.
 *
 * Returns a `Record<string, string>` with the standard property names as *lowercase* keys,
 * ready to merge into the condition-property bag that `evalCondition` + `substituteVars`
 * consume.
 */
export function computeReservedProperties(
    currentFile: string,
    rootProjectPath: string,
): Record<string, string> {
    const thisFile = path.basename(currentFile);
    const thisExt = path.extname(currentFile);
    const thisFileName = thisExt.length > 0 ? thisFile.slice(0, -thisExt.length) : thisFile;
    const thisFileDir = path.dirname(currentFile);
    // Contract: $(MSBuildThisFileDirectory) ends with the path separator — `<Import
    // Project="$(MSBuildThisFileDirectory)Foo.props"/>` concatenates cleanly on both POSIX
    // and Windows.
    const thisFileDirWithSep = thisFileDir.endsWith(path.sep) ? thisFileDir : thisFileDir + path.sep;

    const projFile = path.basename(rootProjectPath);
    const projExt = path.extname(rootProjectPath);
    const projName = projExt.length > 0 ? projFile.slice(0, -projExt.length) : projFile;
    const projDir = path.dirname(rootProjectPath);

    return {
        msbuildthisfile: thisFile,
        msbuildthisfilename: thisFileName,
        msbuildthisfiledirectory: thisFileDirWithSep,
        msbuildthisfilefullpath: currentFile,
        msbuildthisfileextension: thisExt,
        msbuildprojectdirectory: projDir,
        msbuildprojectfile: projFile,
        msbuildprojectname: projName,
        msbuildprojectfullpath: rootProjectPath,
        msbuildprojectextension: projExt,
    };
}

/** Names that users are not allowed to shadow via `<PropertyGroup>`. */
export const RESERVED_PROPERTY_NAMES: ReadonlySet<string> = new Set([
    "msbuildthisfile",
    "msbuildthisfilename",
    "msbuildthisfiledirectory",
    "msbuildthisfilefullpath",
    "msbuildthisfileextension",
    "msbuildprojectdirectory",
    "msbuildprojectfile",
    "msbuildprojectname",
    "msbuildprojectfullpath",
    "msbuildprojectextension",
]);

/** True when `name` (case-insensitive) is a reserved MSBuild property. */
export function isReservedProperty(name: string): boolean {
    return RESERVED_PROPERTY_NAMES.has(name.toLowerCase());
}
