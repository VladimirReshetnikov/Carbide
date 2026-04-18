// Carbide-maintained allow-list of managed-only NuGet packages known to be safe for Mono-WASM
// compilation. Growth is a deliberate PR with a fixture to back the new entry (see M6 plan
// §5 D75). Entries are matched case-insensitively.

export interface AllowListEntry {
    id: string;
    /** Last verified compatible version; informational. The resolver still honours the caller's range. */
    lastVerified: string;
    /** One-line description for diagnostics. */
    description: string;
    /** Upstream project URL for click-through in error messages. */
    source: string;
}

export const ALLOW_LIST: readonly AllowListEntry[] = Object.freeze([
    {
        id: "Newtonsoft.Json",
        lastVerified: "13.0.3",
        description: "Classic JSON serializer for .NET.",
        source: "https://www.nuget.org/packages/Newtonsoft.Json",
    },
    {
        id: "YamlDotNet",
        lastVerified: "15.0.0",
        description: "YAML parser and emitter.",
        source: "https://www.nuget.org/packages/YamlDotNet",
    },
    {
        id: "CsvHelper",
        lastVerified: "32.0.0",
        description: "Library for reading and writing CSV files.",
        source: "https://www.nuget.org/packages/CsvHelper",
    },
    {
        id: "Humanizer.Core",
        lastVerified: "2.14.1",
        description: "Manipulate strings, numbers, and dates in a human-friendly way.",
        source: "https://www.nuget.org/packages/Humanizer.Core",
    },
    {
        id: "NodaTime",
        lastVerified: "3.1.11",
        description: "Alternative date/time API for .NET.",
        source: "https://www.nuget.org/packages/NodaTime",
    },
    {
        id: "Scriban",
        lastVerified: "5.9.1",
        description: "Lightweight template engine for text generation.",
        source: "https://www.nuget.org/packages/Scriban",
    },
    {
        id: "Handlebars.Net",
        lastVerified: "2.1.6",
        description: ".NET port of the Handlebars templating language.",
        source: "https://www.nuget.org/packages/Handlebars.Net",
    },
    {
        id: "Serilog",
        lastVerified: "4.0.0",
        description: "Structured logging library (core).",
        source: "https://www.nuget.org/packages/Serilog",
    },
    {
        id: "Serilog.Sinks.Console",
        lastVerified: "6.0.0",
        description: "Console sink for Serilog (managed-only; safe for Carbide).",
        source: "https://www.nuget.org/packages/Serilog.Sinks.Console",
    },
    {
        id: "FluentAssertions",
        lastVerified: "6.12.0",
        description: "Expressive assertion library (managed-only portions used).",
        source: "https://www.nuget.org/packages/FluentAssertions",
    },
]);

const BY_ID_LOWER: ReadonlyMap<string, AllowListEntry> = new Map(
    ALLOW_LIST.map((e) => [e.id.toLowerCase(), e] as const),
);

export function isAllowed(id: string): boolean {
    return BY_ID_LOWER.has(id.toLowerCase());
}

export function getEntry(id: string): AllowListEntry | undefined {
    return BY_ID_LOWER.get(id.toLowerCase());
}
