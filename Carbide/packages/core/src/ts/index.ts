export const CARBIDE_VERSION = "0.0.0" as const;

export async function initialize(): Promise<string> {
    return "Carbide initialised";
}
