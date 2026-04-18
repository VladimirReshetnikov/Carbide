// Public entry for @carbide/nuget. See carbide-M6-detailed-plan §3.

export { resolve, AllowListRefusedError, SafetyRefusalError } from "./resolver.js";
export { createFlatContainer, DEFAULT_SOURCE_URL, OfflineCacheMissError, type FlatContainer, type FlatContainerOptions } from "./flat-container.js";
export { openDefaultCache, openCache, defaultCacheDir, sha256Hex, type Cache, type CacheMeta } from "./cache.js";
export { ALLOW_LIST, isAllowed, getEntry, type AllowListEntry } from "./allowlist.js";
export { checkSafety, type SafetyResult } from "./safety.js";
export {
    parseVersion,
    parseRange,
    compareVersion,
    contains,
    bestMatch,
    versionEq,
    VersionParseError,
    type Version,
    type VersionRange,
} from "./version-range.js";
export { parseTfm, compatibleLibFolders, pickBestLibFolder, collectLibFolders, libFolderOf, type Tfm, type TfmFamily } from "./tfm-compat.js";
export {
    readNuspec,
    nuspecDepsToPackageRefs,
    type NuspecInfo,
    type NuspecDependency,
    type NuspecDependencyGroup,
} from "./nuspec.js";
export { listEntries, readEntry, ZipParseError, type ZipEntry } from "./zip.js";
export { buildLock, writeLock, readLock, LOCK_SCHEMA_VERSION, LockReadError } from "./lock.js";
export { MSNUGET_CODES } from "./warnings.js";
export type {
    PackageReference,
    ResolveOptions,
    ResolvedGraph,
    ResolvedPackage,
    ResolvedReference,
    ResolveLock,
    AllowListMode,
    Warning,
    WarningSeverity,
} from "./types.js";
