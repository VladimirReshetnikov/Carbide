// Adapted from WasmSharp.Core.CompilationInterop. Expanded to carry the M1 JSExport surface
// (session / project management + build/run) with JSON at the boundary per architecture §5.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using Carbide.Core.Hosting;
using Carbide.Core.Services;
using Carbide.Terminal;
using Microsoft.Extensions.Logging;

namespace Carbide.Core;

public static partial class CompilationInterop
{
    [JSExport]
    public static Task InitAsync(string[] assemblyUrls)
    {
        var logger = Host.Services.GetService<ILogger<SessionSolutions>>();
        logger.LogInformation("Carbide initialising with {Count} assembly urls.", assemblyUrls.Length);
        return Host.Dispatch(s => s.InitializeReferencesAsync(assemblyUrls));
    }

    /// <summary>
    /// U1.2 — set the Carbide.Core logger's minimum level. Call before <see cref="InitAsync"/>
    /// to suppress the initial "Carbide initialising…" info line. Accepts the standard
    /// Microsoft.Extensions.Logging names (case-insensitive): trace, debug, information,
    /// warning, error, critical, none. An unrecognised value leaves the level unchanged.
    /// </summary>
    [JSExport]
    public static void SetLogLevel(string level)
    {
        if (string.IsNullOrWhiteSpace(level)) return;
        var normalised = level.Trim().ToLowerInvariant();
        LogLevel? parsed = normalised switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" or "quiet" => LogLevel.Error,
            "critical" or "crit" => LogLevel.Critical,
            "none" or "silent" => LogLevel.None,
            _ => null,
        };
        if (parsed is not null)
        {
            Carbide.Core.Hosting.WebAssemblyConsoleLoggerConfig.MinLogLevel = parsed.Value;
        }
    }

    [JSExport]
    public static string CreateSession(string optionsJson)
    {
        _ = optionsJson; // M1: options are parsed but no fields are yet consumed at the session level.
        return Host.Dispatch(s => s.CreateSession());
    }

    [JSExport]
    public static void DisposeSession(string sessionId)
        => Host.Dispatch(s => s.DisposeSession(sessionId));

    [JSExport]
    public static string CreateProject(string sessionId, string optionsJson)
    {
        var options = string.IsNullOrWhiteSpace(optionsJson)
            ? null
            : JsonSerializer.Deserialize(optionsJson, CarbideJsonContext.Default.ProjectOptionsDto);
        ValidateSchemaVersion(options?.SchemaVersion, "ProjectOptions");
        var documentOptions = BuildDocumentOptions(options);
        return Host.Dispatch(s => s.CreateProject(sessionId, documentOptions, options?.AssemblyName));
    }

    [JSExport]
    public static void AddSource(string projectId, string path, string code)
        => Host.Dispatch(s => s.AddSource(projectId, path, code));

    [JSExport]
    public static void UpdateSource(string projectId, string path, string code)
        => Host.Dispatch(s => s.UpdateSource(projectId, path, code));

    [JSExport]
    public static void RemoveSource(string projectId, string path)
        => Host.Dispatch(s => s.RemoveSource(projectId, path));

    // --- M3: reference registry surface -------------------------------------------------

    [JSExport]
    public static string AddReference(string sessionId, string base64Bytes, string? name)
    {
        var bytes = Convert.FromBase64String(base64Bytes);
        return Host.Dispatch(s => s.AddReference(sessionId, bytes, name));
    }

    [JSExport]
    public static bool RemoveReference(string sessionId, string referenceId)
        => Host.Dispatch(s => s.RemoveReference(sessionId, referenceId));

    [JSExport]
    public static void AttachReference(string projectId, string referenceId)
        => Host.Dispatch(s => s.AttachReference(projectId, referenceId));

    [JSExport]
    public static async Task<string> GetDiagnosticsAsync(string projectId)
    {
        var diagnostics = await Host.Dispatch(s => s.GetDiagnosticsAsync(projectId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(diagnostics, CarbideJsonContext.Default.DiagnosticArray);
    }

    /// <summary>
    /// Emits PE + portable-PDB bytes without running. Returns a JSON payload with the byte
    /// arrays base64-encoded; the TS side decodes them back into Uint8Array. See M4 plan §5 D38.
    /// </summary>
    [JSExport]
    public static async Task<string> BuildAsync(string projectId)
    {
        var result = await Host.Dispatch(s => s.BuildAsync(projectId)).ConfigureAwait(false);
        var dto = new BuildResultDto
        {
            SchemaVersion = result.SchemaVersion,
            Success = result.Success,
            PeBase64 = result.Pe is null ? null : Convert.ToBase64String(result.Pe),
            PdbBase64 = result.Pdb is null ? null : Convert.ToBase64String(result.Pdb),
            Diagnostics = result.Diagnostics,
            DurationMs = result.DurationMs,
        };
        return JsonSerializer.Serialize(dto, CarbideJsonContext.Default.BuildResultDto);
    }

    /// <summary>
    /// U2 — runs the project. <paramref name="runOptionsJson"/> is a
    /// <c>RunOptionsRequest</c>-shaped JSON string carrying optional program argv and
    /// stdin. An empty / whitespace-only string means "defaults" (empty args, no stdin),
    /// which skips JSON parsing entirely for the common "just run it" case.
    /// </summary>
    [JSExport]
    public static async Task<string> RunAsync(string projectId, string runOptionsJson)
    {
        var (args, stdin) = ParseRunOptions(runOptionsJson);
        var result = await Host.Dispatch(s => s.RunAsync(projectId, args, stdin)).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CarbideJsonContext.Default.RunResult);
    }

    /// <summary>
    /// T1 — interactive run. <paramref name="optionsJson"/> is a
    /// <c>RunInteractiveOptionsRequest</c>-shaped JSON string. The C# side installs
    /// streaming <see cref="Console.Out"/> / <see cref="Console.Error"/> writers that push
    /// each buffered chunk into <c>globalThis.Carbide.Terminal.{write,writeErr}</c> before
    /// invoking the entry point. Resolves with the usual <see cref="RunResult"/> shape once
    /// the program exits and the drain flush completes.
    /// </summary>
    [JSExport]
    public static async Task<string> RunInteractiveAsync(string projectId, string optionsJson)
    {
        var options = ParseInteractiveOptions(optionsJson);
        var result = await Host.Dispatch(s => s.RunInteractiveAsync(projectId, options)).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, CarbideJsonContext.Default.RunResult);
    }

    /// <summary>
    /// T1 — signal the C# side that the interactive session is tearing down. Current T1
    /// implementation is a stub (the drain flush runs inside <c>RunInteractiveAsync</c>'s
    /// own finally block before resolving). T2 will extend this to unblock pending async
    /// reads and unwire the input-side of the bridge.
    /// </summary>
    [JSExport]
    public static void DisposeTerminal(string projectId)
        => Host.Dispatch(s => s.DisposeInteractive(projectId));

    private static (string[] Args, string? Stdin) ParseRunOptions(string? runOptionsJson)
    {
        if (string.IsNullOrWhiteSpace(runOptionsJson))
        {
            return (Array.Empty<string>(), null);
        }

        var dto = JsonSerializer.Deserialize(runOptionsJson, CarbideJsonContext.Default.RunOptionsDto);
        ValidateSchemaVersion(dto?.SchemaVersion, "RunOptions");
        var args = dto?.Args ?? Array.Empty<string>();
        var stdin = dto?.Stdin;
        return (args, stdin);
    }

    private static InteractiveOptions ParseInteractiveOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return InteractiveOptions.Default;
        }
        var dto = JsonSerializer.Deserialize(optionsJson, CarbideJsonContext.Default.RunInteractiveOptionsDto);
        ValidateSchemaVersion(dto?.SchemaVersion, "RunInteractiveOptions");
        var args = dto?.Args ?? Array.Empty<string>();
        var style = (dto?.StderrStyle ?? "plain").ToLowerInvariant() switch
        {
            "dim" => StderrStyle.Dim,
            "red" => StderrStyle.Red,
            _ => StderrStyle.Plain,
        };
        return new InteractiveOptions(args, style);
    }

    private static void ValidateSchemaVersion(int? schemaVersion, string name)
    {
        // M5 bumped the schema to 2. U2 bumped to 3 when RunOptionsRequest landed. T1
        // bumped to 4 when the interactive terminal path landed. Accept 1 / 2 / 3 / 4 so
        // pre-T1 clients keep working; higher numbers are a definite mismatch.
        if (schemaVersion is not null and not 1 and not 2 and not 3 and not 4)
        {
            throw new InvalidOperationException(
                $"Unsupported {name} schemaVersion: expected 1, 2, 3, or 4, got {schemaVersion}.");
        }
    }

    private static DocumentOptions? BuildDocumentOptions(ProjectOptionsDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var parseOptions = DocumentOptions.DefaultParseOptions;
        if (!string.IsNullOrWhiteSpace(dto.LanguageVersion)
            && Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(dto.LanguageVersion, out var langVersion))
        {
            parseOptions = parseOptions.WithLanguageVersion(langVersion);
        }
        if (dto.DefineConstants is { Length: > 0 } defines)
        {
            parseOptions = parseOptions.WithPreprocessorSymbols(defines);
        }

        var compilationOptions = DocumentOptions.DefaultCompilationOptions;
        if (dto.Nullable == true)
        {
            compilationOptions = compilationOptions.WithNullableContextOptions(
                Microsoft.CodeAnalysis.NullableContextOptions.Enable);
        }

        return new DocumentOptions
        {
            CSharpCompilationOptions = compilationOptions,
            CSharpParseOptions = parseOptions,
            ImplicitUsings = dto.ImplicitUsings ?? true,
            AssemblyName = string.IsNullOrWhiteSpace(dto.AssemblyName) ? null : dto.AssemblyName,
            RootNamespace = string.IsNullOrWhiteSpace(dto.RootNamespace) ? null : dto.RootNamespace,
        };
    }
}

internal sealed class ProjectOptionsDto
{
    public int? SchemaVersion { get; set; } = 2;
    public string? TargetFramework { get; set; }
    public string? LanguageVersion { get; set; }
    public bool? Nullable { get; set; }
    public bool? ImplicitUsings { get; set; }
    public string? AssemblyName { get; set; }
    public string? RootNamespace { get; set; }
    public string[]? DefineConstants { get; set; }
}

/// <summary>
/// Wire shape of <see cref="Carbide.Core.Services.BuildResult"/>. PE and PDB bytes are
/// base64-encoded so the JSExport string pipeline can carry them without custom
/// <c>Uint8Array</c> marshalling (M4 plan §5 D38).
/// </summary>
internal sealed class BuildResultDto
{
    public int SchemaVersion { get; set; } = 4;
    public bool Success { get; set; }
    public string? PeBase64 { get; set; }
    public string? PdbBase64 { get; set; }
    public Carbide.Core.Services.Diagnostic[] Diagnostics { get; set; } = [];
    public double DurationMs { get; set; }
}

/// <summary>
/// U2 — wire shape of <c>RunOptionsRequest</c>. Parsed from the second argument of the
/// <c>RunAsync</c> JSExport when the TS caller provides non-default argv or stdin.
/// </summary>
internal sealed class RunOptionsDto
{
    public int? SchemaVersion { get; set; } = 3;
    public string[]? Args { get; set; }
    public string? Stdin { get; set; }
}

/// <summary>
/// T1 — wire shape of <c>RunInteractiveOptionsRequest</c>. Parsed from the second argument
/// of the <c>RunInteractiveAsync</c> JSExport. Unlike <see cref="RunOptionsDto"/>, the
/// interactive path always carries a JSON payload; no empty-string fast path exists.
/// </summary>
internal sealed class RunInteractiveOptionsDto
{
    public int? SchemaVersion { get; set; } = 4;
    public string[]? Args { get; set; }
    /// <summary>One of "plain", "dim", "red"; case-insensitive. Default: "plain".</summary>
    public string? StderrStyle { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.Strict,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(Diagnostic[]), TypeInfoPropertyName = "DiagnosticArray")]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(BuildResultDto))]
[JsonSerializable(typeof(ProjectOptionsDto))]
[JsonSerializable(typeof(RunOptionsDto))]
[JsonSerializable(typeof(RunInteractiveOptionsDto))]
internal sealed partial class CarbideJsonContext : JsonSerializerContext
{
}
