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
using System.Text;
using Carbide.Core.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class ProjectCompiler
{
    private readonly ILogger<ProjectCompiler> _logger = Host.Services.GetService<ILogger<ProjectCompiler>>();
    private readonly ReferenceRegistry _referenceRegistry;
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

    public ProjectCompiler(ReferenceRegistry referenceRegistry, string? assemblyName = null, DocumentOptions? options = null)
    {
        _referenceRegistry = referenceRegistry ?? throw new ArgumentNullException(nameof(referenceRegistry));
        options ??= DocumentOptions.Default;
        // M5: DocumentOptions.AssemblyName takes precedence over the constructor default so
        // .csproj-derived options override the caller's fallback.
        _assemblyName = options.AssemblyName ?? assemblyName ?? "Carbide.Project";

        Workspace = new AdhocWorkspace();
        _projectId = ProjectId.CreateNewId();

        Solution = Workspace.CurrentSolution
            .AddProject(_projectId, _assemblyName, _assemblyName, LanguageNames.CSharp);

        // Hidden implicit-usings document. When the caller opts out via ImplicitUsings=false
        // (set by parseCsproj from <ImplicitUsings>disable</ImplicitUsings>), skip the
        // document entirely so user code that expects strict namespace discipline isn't
        // silently relaxed.
        if (options.ImplicitUsings)
        {
            _implicitUsingsId = DocumentId.CreateNewId(_projectId, ImplicitUsingsDocumentPath);
            Solution = Solution.AddDocument(
                _implicitUsingsId,
                ImplicitUsingsDocumentPath,
                SourceText.From(ImplicitUsingsSource, Encoding.UTF8),
                filePath: ImplicitUsingsDocumentPath);
        }
        else
        {
            _implicitUsingsId = null;
        }

        Solution = Solution.AddMetadataReferences(_projectId, MetadataReferenceCache.MetadataReferences);
        Solution = Solution.WithProjectCompilationOptions(_projectId, options.CSharpCompilationOptions);
        Solution = Solution.WithProjectParseOptions(_projectId, options.CSharpParseOptions);
    }

    public AdhocWorkspace Workspace { get; }
    public Solution Solution { get; private set; }

    private readonly DocumentId? _implicitUsingsId;

    // Path → DocumentId for user-added documents only. The implicit-usings document is
    // intentionally not in this map so it can't leak into AddSource/UpdateSource/RemoveSource.
    private readonly Dictionary<string, DocumentId> _documentsByPath = new(StringComparer.Ordinal);

    // Set of attached reference ids (session-scoped). Looked up through _referenceRegistry at
    // rebuild time — if the registry removes a ref while it's still attached here, the next
    // rebuild silently skips the orphan (architecture §3.1).
    private readonly HashSet<string> _attachedReferenceIds = new(StringComparer.Ordinal);

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
        // Encoding is required so portable-PDB emission in BuildAsync can record source
        // offsets. Without it, Roslyn surfaces CS8055 at emit time.
        Solution = Solution.AddDocument(id, path, SourceText.From(code, Encoding.UTF8), filePath: path);
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

        Solution = Solution.WithDocumentText(id, SourceText.From(code, Encoding.UTF8));
        Workspace.TryApplyChanges(Solution);
        _logger.LogTrace("UpdateSource('{Path}').", path);
    }

    /// <summary>
    /// Attaches a session-registered reference to this project. Idempotent — attaching the
    /// same id twice is a no-op.
    /// </summary>
    public void AttachReference(string referenceId)
    {
        if (string.IsNullOrEmpty(referenceId))
        {
            throw new ArgumentException("Reference id must be non-empty.", nameof(referenceId));
        }
        if (!_referenceRegistry.Contains(referenceId))
        {
            throw new InvalidOperationException(
                $"Unknown reference id '{referenceId}'. Register bytes via AddReference first.");
        }
        if (_attachedReferenceIds.Add(referenceId))
        {
            RebuildMetadataReferences();
            _logger.LogTrace("AttachReference('{Id}') -> now {Count} attached.", referenceId, _attachedReferenceIds.Count);
        }
    }

    /// <summary>
    /// Detaches a reference from this project. No-op if not attached. Called by SessionSolutions
    /// when a reference is removed from the session registry, so every affected project's
    /// solution drops the stale reference on the spot.
    /// </summary>
    public void DetachReference(string referenceId)
    {
        if (_attachedReferenceIds.Remove(referenceId))
        {
            RebuildMetadataReferences();
            _logger.LogTrace("DetachReference('{Id}') -> now {Count} attached.", referenceId, _attachedReferenceIds.Count);
        }
    }

    private void RebuildMetadataReferences()
    {
        // Compile-time references = built-in BCL cache ∪ currently-attached user references.
        // Orphaned ids (attached but removed from the registry) are silently skipped.
        var refs = new List<MetadataReference>(MetadataReferenceCache.MetadataReferences);
        foreach (var id in _attachedReferenceIds)
        {
            if (_referenceRegistry.TryGet(id, out var reference))
            {
                refs.Add(reference);
            }
        }
        Solution = Solution.WithProjectMetadataReferences(_projectId, refs);
        Workspace.TryApplyChanges(Solution);
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
        // Apply the same OutputKind inference as BuildAsync so library projects (no
        // top-level statements, no Main) don't trip CS5001 during validate. Aligns
        // `validate` with `build` for multi-project graphs where libraries are routine.
        var outputKind = InferOutputKind(compilation);
        if (compilation.Options is CSharpCompilationOptions csOptions && csOptions.OutputKind != outputKind)
        {
            compilation = compilation.WithOptions(csOptions.WithOutputKind(outputKind));
        }
        return compilation.GetDiagnostics().ToCarbideDiagnosticArray();
    }

    /// <summary>
    /// Emits the compilation as PE + portable-PDB bytes. Returns a <see cref="BuildResult"/>
    /// that is ready to hand to JS callers through <c>CompilationInterop.BuildAsync</c>.
    ///
    /// <para>Auto-selects <see cref="OutputKind.ConsoleApplication"/> if any syntax tree has
    /// top-level statements (C# 9+) or a suitable <c>Main</c>, otherwise
    /// <see cref="OutputKind.DynamicallyLinkedLibrary"/>. This lets both library code
    /// ("just build a DLL of these types") and top-level-statements apps build via the
    /// same entry point without caller configuration (M5 bugfix for CS8805).</para>
    /// </summary>
    public async Task<BuildResult> BuildAsync()
    {
        var sw = Stopwatch.StartNew();
        var project = Solution.GetProject(_projectId)
            ?? throw new InvalidOperationException("Project missing from solution.");
        var initial = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");
        var outputKind = InferOutputKind(initial);
        var (compilation, preEmitDiagnostics) = await TryGetErrorFreeCompilationAsync(outputKind).ConfigureAwait(false);
        if (compilation is null)
        {
            sw.Stop();
            return BuildResult.Failed(preEmitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        var (pe, pdb, emitDiagnostics) = EmitPeAndPdb(compilation);
        sw.Stop();
        return pe is null
            ? BuildResult.Failed(emitDiagnostics, sw.Elapsed.TotalMilliseconds)
            : BuildResult.Succeeded(pe, pdb!, sw.Elapsed.TotalMilliseconds);
    }

    private static OutputKind InferOutputKind(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var roslynRoot = tree.GetRoot();
            foreach (var member in roslynRoot.ChildNodes())
            {
                if (member is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax)
                {
                    return OutputKind.ConsoleApplication;
                }
            }
        }
        return OutputKind.DynamicallyLinkedLibrary;
    }

    /// <summary>
    /// Fetches the compilation with the requested output kind, bails with diagnostics when
    /// pre-emit errors exist, and otherwise returns the compilation for emission or
    /// execution. Centralises the "get compilation + filter for errors" dance that both
    /// <see cref="BuildAsync"/> and <see cref="RunAsync"/> perform, and lets each call
    /// control whether CS5001 "no suitable Main" is an error or not.
    /// </summary>
    private async Task<(Compilation? Compilation, Diagnostic[] Diagnostics)> TryGetErrorFreeCompilationAsync(OutputKind outputKind)
    {
        var project = Solution.GetProject(_projectId)
            ?? throw new InvalidOperationException("Project missing from solution.");
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no compilation.");

        if (compilation.Options is CSharpCompilationOptions csOptions && csOptions.OutputKind != outputKind)
        {
            compilation = compilation.WithOptions(csOptions.WithOutputKind(outputKind));
        }

        var diagnostics = compilation.GetDiagnostics();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return (null, diagnostics.ToCarbideDiagnosticArray());
        }
        return (compilation, Array.Empty<Diagnostic>());
    }

    /// <summary>
    /// Emits PE + portable-PDB from an already-validated compilation. On emit failure, PE is
    /// null and the emit diagnostics come back instead.
    /// </summary>
    private static (byte[]? Pe, byte[]? Pdb, Diagnostic[] Diagnostics) EmitPeAndPdb(Compilation compilation)
    {
        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var options = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);
        var emit = compilation.Emit(peStream, pdbStream, options: options);
        if (!emit.Success)
        {
            return (null, null, emit.Diagnostics.ToCarbideDiagnosticArray());
        }
        return (peStream.ToArray(), pdbStream.ToArray(), Array.Empty<Diagnostic>());
    }

    public async Task<RunResult> RunAsync()
    {
        var sw = Stopwatch.StartNew();
        var (compilation, preEmitDiagnostics) = await TryGetErrorFreeCompilationAsync(OutputKind.ConsoleApplication).ConfigureAwait(false);
        if (compilation is null)
        {
            return RunResult.CompileFailure(preEmitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        var (peBytes, _, emitDiagnostics) = EmitPeAndPdb(compilation);
        if (peBytes is null)
        {
            return RunResult.CompileFailure(emitDiagnostics, sw.Elapsed.TotalMilliseconds);
        }

        var entryPoint = compilation.GetEntryPoint(CancellationToken.None)
            ?? throw new InvalidOperationException("No entry point discovered in compilation.");

        // Pre-load every attached reference so the runtime can resolve them by identity when
        // the user's PE references their types. Key the loaded assemblies by simple name so
        // the AssemblyResolve handler below can answer requests that arrive during JIT.
        var loadedReferences = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var refId in _attachedReferenceIds)
        {
            if (!_referenceRegistry.TryGetBytes(refId, out var refBytes))
            {
                continue;
            }
            try
            {
                var refAssembly = LoadAssembly(refBytes);
                var simpleName = refAssembly.GetName().Name;
                if (!string.IsNullOrEmpty(simpleName))
                {
                    loadedReferences[simpleName] = refAssembly;
                }
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
            {
                _logger.LogWarning(
                    "Attached reference '{Id}' could not be loaded into the AppDomain: {Message}",
                    refId, ex.Message);
            }
        }

        // Mono-WASM's default AssemblyLoadContext does not always find assemblies loaded via
        // Assembly.Load(byte[]) when resolving references from a later Assembly.Load(byte[]).
        // An AssemblyResolve handler fills the gap by answering by simple name.
        ResolveEventHandler resolveHandler = (sender, args) =>
        {
            var simpleName = new AssemblyName(args.Name).Name;
            return simpleName is not null && loadedReferences.TryGetValue(simpleName, out var found)
                ? found
                : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;

        Assembly assembly;
        try
        {
            assembly = LoadAssembly(peBytes);
        }
        finally
        {
            // Keep the handler alive for the duration of the user's run so lazy references
            // (types resolved at method-JIT time) can still find the assemblies. Removal
            // happens in the outer finally after the user's entry point has returned.
        }
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
            AppDomain.CurrentDomain.AssemblyResolve -= resolveHandler;
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
