// Adapted from WasmSharp.Core.Services.CodeSession.
// M1 shape (single document) is superseded here by M2's path-keyed document dictionary.
// Extensions over upstream:
//   * Compilation.GetEntryPoint used for entry-point discovery (see WasmSharp#5).
//   * Task / Task<int> / int return types from Main handled.
//   * stderr is captured alongside stdout.
//   * Duration and exit code reported in RunResult.
//   * Multi-document support with AddSource / UpdateSource / RemoveSource (M2).
//   * Hidden implicit-usings document always present so bare Console.WriteLine compiles.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Carbide.Core.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class ProjectCompiler
{
    private readonly ILogger<ProjectCompiler> _logger = Host.Services.GetService<ILogger<ProjectCompiler>>();
    private readonly ProjectId _projectId;
    private readonly string _assemblyName;

    /// <summary>
    /// Default implicit-usings set for console applications. Mirrors the SDK's ImplicitUsings
    /// behaviour so bare <c>Console.WriteLine("hello")</c> compiles without an explicit
    /// <c>using System;</c>.
    /// </summary>
    private const string ImplicitUsingsSource = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    /// <summary>Reserved path — callers may not Add/Update/Remove this document.</summary>
    internal const string ImplicitUsingsDocumentPath = "Carbide.GlobalUsings.g.cs";

    /// <summary>Default path used when callers don't supply one (kept for M1 compatibility).</summary>
    public const string DefaultDocumentPath = "Program.cs";

    public ProjectCompiler(string? assemblyName = null, DocumentOptions? options = null)
    {
        options ??= DocumentOptions.Default;
        _assemblyName = assemblyName ?? "Carbide.Project";

        Workspace = new AdhocWorkspace();
        _projectId = ProjectId.CreateNewId();

        Solution = Workspace.CurrentSolution
            .AddProject(_projectId, _assemblyName, _assemblyName, LanguageNames.CSharp);

        // Hidden implicit-usings document so bare Console.WriteLine compiles. Reserved path:
        // callers cannot Add / Update / Remove it.
        _implicitUsingsId = DocumentId.CreateNewId(_projectId, ImplicitUsingsDocumentPath);
        Solution = Solution.AddDocument(
            _implicitUsingsId,
            ImplicitUsingsDocumentPath,
            SourceText.From(ImplicitUsingsSource),
            filePath: ImplicitUsingsDocumentPath);

        Solution = Solution.AddMetadataReferences(_projectId, MetadataReferenceCache.MetadataReferences);
        Solution = Solution.WithProjectCompilationOptions(_projectId, options.CSharpCompilationOptions);
        Solution = Solution.WithProjectParseOptions(_projectId, options.CSharpParseOptions);
    }

    public AdhocWorkspace Workspace { get; }
    public Solution Solution { get; private set; }

    private readonly DocumentId _implicitUsingsId;

    // Path → DocumentId for user-added documents only. The implicit-usings document is
    // intentionally not in this map so it can't leak into AddSource/UpdateSource/RemoveSource.
    private readonly Dictionary<string, DocumentId> _documentsByPath = new(StringComparer.Ordinal);

    /// <summary>
    /// The set of user-visible document paths currently in the project, in insertion order.
    /// Exposed for diagnostics and tests; callers must not mutate it.
    /// </summary>
    public IReadOnlyCollection<string> DocumentPaths => _documentsByPath.Keys;

    public void AddSource(string path, string code)
    {
        ValidatePath(path);
        if (_documentsByPath.ContainsKey(path))
        {
            throw new InvalidOperationException(
                $"Document path '{path}' is already in the project; use UpdateSource to replace its content. " +
                "Note: paths are compared byte-for-byte, so casing and slash direction matter.");
        }

        var id = DocumentId.CreateNewId(_projectId, path);
        Solution = Solution.AddDocument(id, path, SourceText.From(code), filePath: path);
        _documentsByPath[path] = id;
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("AddSource('{Path}') -> now {Count} user document(s).", path, _documentsByPath.Count);
    }

    public void UpdateSource(string path, string code)
    {
        ValidatePath(path);
        if (!_documentsByPath.TryGetValue(path, out var id))
        {
            throw new InvalidOperationException(
                $"Unknown document path '{path}'; call AddSource first. " +
                "Note: paths are compared byte-for-byte, so casing and slash direction matter.");
        }

        Solution = Solution.WithDocumentText(id, SourceText.From(code));
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("UpdateSource('{Path}').", path);
    }

    public void RemoveSource(string path)
    {
        ValidatePath(path);
        if (!_documentsByPath.TryGetValue(path, out var id))
        {
            // Silent no-op per architecture M2 D16: teardown code commonly tries to clean
            // multiple files without knowing which survived the last failure.
            _logger.LogTrace("RemoveSource('{Path}'): not present, no-op.", path);
            return;
        }

        Solution = Solution.RemoveDocument(id);
        _documentsByPath.Remove(path);
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("RemoveSource('{Path}') -> now {Count} user document(s).", path, _documentsByPath.Count);
    }

    private static void ValidatePath(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (path.Length == 0)
        {
            throw new ArgumentException("Document path must not be empty.", nameof(path));
        }
        if (string.Equals(path, ImplicitUsingsDocumentPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{ImplicitUsingsDocumentPath}' is a reserved path owned by Carbide's implicit-usings machinery.");
        }
    }

    public async Task<Diagnostic[]> GetDiagnosticsAsync()
    {
        using var _ = new Tracer(nameof(GetDiagnosticsAsync));
        var project = Solution.GetProject(_projectId)
            ?? throw new InvalidOperationException("Project missing from solution.");
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");
        return compilation.GetDiagnostics().ToCarbideDiagnosticArray();
    }

    public async Task<RunResult> RunAsync()
    {
        var sw = Stopwatch.StartNew();
        var project = Solution.GetProject(_projectId)
            ?? throw new InvalidOperationException("Project missing from solution.");
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");

        var compileDiagnostics = compilation.GetDiagnostics();
        if (compileDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return RunResult.CompileFailure(compileDiagnostics.ToCarbideDiagnosticArray(), sw.Elapsed.TotalMilliseconds);
        }

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);
        if (!emit.Success)
        {
            return RunResult.CompileFailure(emit.Diagnostics.ToCarbideDiagnosticArray(), sw.Elapsed.TotalMilliseconds);
        }

        var peBytes = peStream.ToArray();
        var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
            ?? throw new InvalidOperationException("No entry point discovered in compilation.");

        var assembly = LoadAssembly(peBytes);
        var reflectedEntry = assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"Entry point '{entryPoint.ContainingType}.{entryPoint.Name}' was resolved by Roslyn but not reflected from the emitted assembly.");

        using var stdOutCapture = new StringWriter();
        using var stdErrCapture = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        Console.SetOut(stdOutCapture);
        Console.SetError(stdErrCapture);

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            var parameters = reflectedEntry.GetParameters();
            var invocationArgs = parameters.Length == 0
                ? Array.Empty<object?>()
                : new object?[] { Array.Empty<string>() };

            object? result;
            try
            {
                result = reflectedEntry.Invoke(null, invocationArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }

            switch (result)
            {
                case Task<int> taskInt:
                    exitCode = await taskInt.ConfigureAwait(false);
                    break;
                case Task task:
                    await task.ConfigureAwait(false);
                    break;
                case int i:
                    exitCode = i;
                    break;
                case ValueTask<int> vti:
                    exitCode = await vti.ConfigureAwait(false);
                    break;
                case ValueTask vt:
                    await vt.ConfigureAwait(false);
                    break;
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            uncaught = ex.ToString();
            await stdErrCapture.WriteLineAsync(uncaught).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }

        sw.Stop();
        var stdOut = stdOutCapture.ToString();
        var stdErr = stdErrCapture.ToString();

        return uncaught is null
            ? RunResult.Success_(stdOut, stdErr, exitCode, sw.Elapsed.TotalMilliseconds)
            : RunResult.Uncaught(stdOut, stdErr, uncaught, sw.Elapsed.TotalMilliseconds);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "The in-memory emitted assembly is the user's program; reflection into it is expected and not subject to trimming.")]
    private static Assembly LoadAssembly(byte[] bytes) => Assembly.Load(bytes);
}
