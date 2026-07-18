# Third-party notices

`@carbide/core` is licensed under Apache-2.0 for Carbide-authored work. This file identifies source and binary material distributed with the package under other terms. WasmSharp-derived Apache-2.0 source provenance is recorded separately in [`ATTRIBUTION.md`](ATTRIBUTION.md).

## Source adaptations and replacements

The following MIT-licensed .NET Foundation material is incorporated into source or binaries shipped by this package:

- `src/Hosting/WebAssemblyConsoleLogger.cs`, adapted from `dotnet/aspnetcore` through WasmSharp.
- `src/Terminal/KeyParser.cs`, adapted from `dotnet/runtime`.
- `src/ts/runtime/dotnet-types.ts`, a reduced and adapted subset of `dotnet/runtime`'s generated host definitions.
- The replacement `System.Console.dll`, which contains .NET-derived `Console.cs`, `ConsoleCancelEventArgs.cs`, `ConsoleColor.cs`, `ConsoleKey.cs`, `ConsoleKeyInfo.cs`, `ConsoleModifiers.cs`, `ConsoleSpecialKey.cs`, and `microsoft-net-public-key.snk` from `dotnet/runtime`'s `System.Console` library.

The applicable .NET MIT license is reproduced in [`third-party/dotnet/LICENSE.TXT`](third-party/dotnet/LICENSE.TXT). File headers and [`ATTRIBUTION.md`](ATTRIBUTION.md) preserve detailed provenance.

## Published `_framework` payload

The npm package contains the Mono WebAssembly runtime, .NET framework assemblies, Roslyn, and supporting managed libraries. These components are not relicensed as Carbide work:

| Component | Version(s) | Upstream terms and bundled notices |
|---|---|---|
| .NET Runtime / Mono WebAssembly | 10.0.6 | MIT; [`LICENSE.TXT`](third-party/dotnet/LICENSE.TXT) and the runtime's complete [`THIRD-PARTY-NOTICES.TXT`](third-party/dotnet/THIRD-PARTY-NOTICES.TXT). |
| Jab source generator | 0.11.0 | MIT, copyright Pavel Krymets; generated dependency-injection source is compiled into `Carbide.Core.dll`. The exact license from the [pinned upstream commit](https://github.com/pakrym/jab/tree/10557c9b3b098a9fe9364e9e181e3482681e3a66) is included as [`third-party/jab/LICENSE`](third-party/jab/LICENSE). |
| Microsoft.CodeAnalysis (Roslyn), including CSharp, Workspaces, Features, Scripting, and AnalyzerUtilities | 4.14.0 | MIT; upstream notices in [`third-party/roslyn/`](third-party/roslyn/) and [`third-party/roslyn-analyzers/`](third-party/roslyn-analyzers/). |
| Microsoft.CodeAnalysis.Elfie | 1.0.0 | MIT. |
| Microsoft.DiaSymReader | 2.0.0 | MIT. |
| Humanizer.Core | 2.14.1 | MIT, copyright .NET Foundation and Contributors. |
| Microsoft.Extensions.DependencyInjection.Abstractions and Microsoft.Extensions.Logging.Abstractions | 10.0.0-preview.5.25277.114 | MIT; exact package [`LICENSE.TXT`](third-party/dotnet-extensions-preview/LICENSE.TXT) and complete [`THIRD-PARTY-NOTICES.TXT`](third-party/dotnet-extensions-preview/THIRD-PARTY-NOTICES.TXT). |
| System.Composition family, System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, and System.Security.Cryptography.ProtectedData | 9.0.0 | MIT; exact package [`LICENSE.TXT`](third-party/dotnet-9/LICENSE.TXT) and complete [`THIRD-PARTY-NOTICES.TXT`](third-party/dotnet-9/THIRD-PARTY-NOTICES.TXT). |
| System.Data.DataSetExtensions | 4.5.0 | MIT; exact package [`LICENSE.TXT`](third-party/dotnet-corefx-4.5/LICENSE.TXT) and complete [`THIRD-PARTY-NOTICES.TXT`](third-party/dotnet-corefx-4.5/THIRD-PARTY-NOTICES.TXT). |

The notice files above may enumerate additional embedded components under BSD, Unicode, zlib, and other permissive terms. Those texts govern the corresponding upstream components.
