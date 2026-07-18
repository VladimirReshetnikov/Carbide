# Carbide System.Console browser fork

This project builds Carbide's browser-specific replacement for `System.Console.dll`. It preserves the stock .NET assembly identity while routing console behavior through Carbide's browser terminal bridge.

## License and provenance

Carbide-authored project, build, and browser-implementation files in this directory are licensed under the repository's [Apache License 2.0](../../../../LICENSE). Selected source files are copied from or substantially derived from the .NET runtime and retain their upstream MIT terms; [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) defines that file-level scope and reproduces the license.

The upstream source is [dotnet/runtime's `System.Console` library at .NET 10.0.0](https://github.com/dotnet/runtime/tree/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Console). The following files retain .NET Foundation MIT headers and are copied or lightly adapted from that source:

- `src/ConsoleCancelEventArgs.cs`
- `src/ConsoleColor.cs`
- `src/ConsoleKey.cs`
- `src/ConsoleKeyInfo.cs`
- `src/ConsoleModifiers.cs`
- `src/ConsoleSpecialKey.cs`

`src/Console.cs` is a substantially modified fork of the upstream public surface. `src/ConsolePal.Browser.cs` was written from scratch against the .NET 10.0.0 `ConsolePal` contract as Carbide's browser implementation. The remaining project/build files are Carbide-authored. The MIT notices on derived files are preserved without changing the Apache-2.0 license of the surrounding Carbide work.

See the [T3 implementation record](../../../docs/planning/milestones/carbide-T3-detailed-plan__2026-04-20__13-56-27-000000.md) for the design and behavioral differences from the stock library.
