// Minimal structural view of @carbide/core's BuildResult. Kept as a local type so the
// launcher carries no runtime dependency on @carbide/core (plan UI-I9). At UI-M3 this
// aligns with @carbide/core's public BuildResult, including the two optional fields
// added by proposal §10.3 (peSchemaVersion, primaryAssemblyName).
export interface BuildResult {
    readonly success: boolean;
    readonly pe?: Uint8Array;
    readonly pdb?: Uint8Array;
    readonly primaryAssemblyName?: string;
    readonly peSchemaVersion?: number;
}
