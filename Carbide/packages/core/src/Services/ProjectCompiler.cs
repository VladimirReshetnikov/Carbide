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
using System.Threading;
using Carbide.Core.Hosting;
using Carbide.Terminal;
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

    public Task<RunResult> RunAsync() => RunAsync(Array.Empty<string>(), null);

    /// <summary>
    /// U2 — compile and run. <paramref name="args"/> is forwarded to the entry point's
    /// <c>Main(string[])</c> parameter (bound by parameter count: 1 string[] = forward,
    /// otherwise ignored). <paramref name="stdin"/> pre-seeds <see cref="Console.In"/> with
    /// a <see cref="System.IO.StringReader"/> when non-null; <see cref="Console.In"/> is
    /// restored in the finally block alongside stdout/stderr.
    /// </summary>
    public async Task<RunResult> RunAsync(string[] args, string? stdin)
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
        // U2: Mono-WASM marks Console.In / Console.SetIn with UnsupportedOSPlatform("browser")
        // AND throws PlatformNotSupportedException at runtime when these APIs touch the
        // real stdin handle. Setting the internal static field via reflection bypasses
        // both the code-analysis warning and the runtime guard: once the field is non-null,
        // the getter returns our TextReader without going through EnsureInitialized.
        System.IO.TextReader? oldIn = null;
        if (stdin is not null)
        {
            oldIn = GetConsoleInField();
            SetConsoleInField(new System.IO.StringReader(stdin));
        }
        Console.SetOut(stdOutCapture);
        Console.SetError(stdErrCapture);

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            var parameters = reflectedEntry.GetParameters();
            // U2: bind by parameter count. `Main()` gets no args; `Main(string[] args)`
            // (including Roslyn's synthesised top-level-statements wrapper) gets the
            // forwarded array. Any other shape falls back to the empty-array behaviour.
            var invocationArgs = parameters.Length == 0
                ? Array.Empty<object?>()
                : new object?[] { args };

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
            if (stdin is not null)
            {
                // Restore whatever was there before (including null — the default "never
                // initialised" state on Mono-WASM).
                SetConsoleInField(oldIn);
            }
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

    // U2 — reflection into Console's internal `s_in` (or `_in`) field. Bypasses both the
    // `[UnsupportedOSPlatform("browser")]` code-analysis guard and the runtime-side
    // PlatformNotSupportedException that Console.In / SetIn throw on Mono-WASM. The field
    // name differs between .NET versions; we probe the two historic names and cache the
    // resolved FieldInfo (once resolved, it's cheap). Returns null when the field is not
    // found — in that case stdin forwarding silently becomes a no-op and the user program
    // gets the default "empty" Console.In behaviour.
    private static System.Reflection.FieldInfo? s_cachedConsoleInField;
    private static System.Reflection.FieldInfo? ResolveConsoleInField()
    {
        if (s_cachedConsoleInField is not null) return s_cachedConsoleInField;
        var t = typeof(Console);
        var field = t.GetField("s_in", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? t.GetField("_in", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        s_cachedConsoleInField = field;
        return field;
    }

    private static System.IO.TextReader? GetConsoleInField()
    {
        var field = ResolveConsoleInField();
        return field?.GetValue(null) as System.IO.TextReader;
    }

    private static void SetConsoleInField(System.IO.TextReader? value)
    {
        var field = ResolveConsoleInField();
        field?.SetValue(null, value);
    }

    /// <summary>
    /// RAII helper so <c>RunInteractiveAsync</c>'s `using` pattern unwinds the
    /// <see cref="TerminalInputState"/> registry slot deterministically on both the
    /// success and failure paths, without an explicit try/finally pair around the new
    /// state lifetime.
    /// </summary>
    private sealed class InputStateDisposer(TerminalInputState state) : IDisposable
    {
        public void Dispose() => state.Dispose();
    }

    /// <summary>
    /// T1 — compile and run interactively. Mirrors <see cref="RunAsync(string[], string?)"/>
    /// but installs <see cref="Carbide.Terminal.StreamingStdOutWriter"/> instances that push
    /// buffered chunks through <see cref="Carbide.Terminal.CarbideTerminalInterop"/> into
    /// the JS terminal bridge while the program runs. Bytes are teed into a
    /// <see cref="StringBuilder"/> so the returned <see cref="RunResult"/> still carries the
    /// full transcript (parity with <c>project.run()</c>'s return shape). Stderr bytes are
    /// SGR-wrapped per <see cref="Carbide.Terminal.InteractiveOptions.StderrStyle"/> on
    /// their way to the bridge, but the tee captures unwrapped text. T1 is output-only;
    /// <see cref="Console.In"/> stays disconnected — stdin lands in T2.
    /// </summary>
    public async Task<RunResult> RunInteractiveAsync(string projectId, InteractiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        // T2 — Per-run input state: reader for Console.In, resize cache, Ctrl+C
        // handlers, cancellation token. Dispose in the finally block to pull the state
        // out of the projectId->state registry and close the reader (so a pending
        // ReadLineAsync resolves to null/EOF).
        var inputState = TerminalInputState.Create(projectId);
        using var _inputStateDisposer = new InputStateDisposer(inputState);

        // T2 — install CarbideSyncContext. T2.1 Option F experiment removed this and
        // empirically confirmed it did not fix the Assembly.Load-plus-await trap;
        // restored.
        var oldSyncContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(CarbideSyncContext.Instance);

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

        // Reference pre-load and AssemblyResolve wiring — mirrors RunAsync. The duplication
        // is intentional: keeping the two run paths independent is cheaper than factoring
        // out a shared helper that has to carry the entire instance surface (_attachedReferenceIds,
        // _referenceRegistry, _logger). If a third run path lands, revisit the refactor.
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

        ResolveEventHandler resolveHandler = (sender, args) =>
        {
            var simpleName = new AssemblyName(args.Name).Name;
            return simpleName is not null && loadedReferences.TryGetValue(simpleName, out var found)
                ? found
                : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;

        var assembly = LoadAssembly(peBytes);
        var reflectedEntry = assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"Entry point '{entryPoint.ContainingType}.{entryPoint.Name}' was resolved by Roslyn but not reflected from the emitted assembly.");

        // Tee each flushed chunk into a StringBuilder (for the RunResult trailer) while also
        // pushing it through the JS bridge. Stderr gets its SGR wrap on the JS-bound path
        // only, so the tee captures raw text (matches what user code wrote).
        var stdOutTee = new StringBuilder();
        var stdErrTee = new StringBuilder();
        var stdOutSink = (string text) =>
        {
            stdOutTee.Append(text);
            CarbideTerminalInterop.WriteStdOut(text);
        };
        var stdErrJsSink = StderrSink.Wrap(
            static text => CarbideTerminalInterop.WriteStdErr(text),
            options.StderrStyle);
        var stdErrSink = (string text) =>
        {
            stdErrTee.Append(text);
            stdErrJsSink(text);
        };

        using var stdOutWriter = new StreamingStdOutWriter(stdOutSink);
        using var stdErrWriter = new StreamingStdOutWriter(stdErrSink);

        var oldOut = Console.Out;
        var oldError = Console.Error;
        Console.SetOut(stdOutWriter);
        Console.SetError(stdErrWriter);

        // T2 — install the BrowserTerminalReader into Console._in via the U2 reflection path
        // so Console.In.ReadLineAsync() resolves against the same queue CarbideConsole uses.
        // Preserve whatever was in the slot before (null in the normal case) and restore in
        // finally. `SetConsoleInField` is the same helper U2 uses for pre-seeded stdin.
        var oldIn = GetConsoleInField();
        SetConsoleInField(inputState.Reader);

        // T3 — signal the forked System.Console.dll that an interactive bridge is live so
        // the fork's cosmetic emitters (`Console.ForegroundColor`, `Console.Clear()`, etc.)
        // emit ANSI instead of throwing. Outside this region the flag is false and those
        // members throw PlatformNotSupportedException with a "use runInteractive" message
        // — preserving the pre-T3 contract for plain `Project.run` programs.
        AppContext.SetData("Carbide.InteractiveBridge", true);

        // T3.1 defensive — re-install before user-code invoke in case the intervening
        // Roslyn await cleared the SC.
        SynchronizationContext.SetSynchronizationContext(CarbideSyncContext.Instance);

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            var parameters = reflectedEntry.GetParameters();
            var invocationArgs = parameters.Length == 0
                ? Array.Empty<object?>()
                : new object?[] { options.Args };

            object? result;
            try
            {
                result = reflectedEntry.Invoke(null, invocationArgs);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }

            // T3.1 defensive — Do NOT use `ConfigureAwait(false)` here. Mono-WASM browser's
            // default `TaskScheduler` dispatches to an absent thread pool; keeping the
            // captured context (our inline-Post `CarbideSyncContext`) avoids that fall-
            // through. Does not on its own fix T2.1 for suspended-completion awaits.
            switch (result)
            {
                case Task<int> taskInt:
                    exitCode = await taskInt;
                    break;
                case Task task:
                    await task;
                    break;
                case int i:
                    exitCode = i;
                    break;
                case ValueTask<int> vti:
                    exitCode = await vti;
                    break;
                case ValueTask vt:
                    await vt;
                    break;
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Walk inner exceptions so the stderr carries the full chain — useful for
            // diagnosing Mono-WASM async failures where the outer wrapper hides the real
            // throwing site (e.g. TaskSchedulerException -> PlatformNotSupportedException).
            var chain = new StringBuilder();
            for (var e = (Exception?)ex; e is not null; e = e.InnerException)
            {
                if (chain.Length > 0) chain.AppendLine().AppendLine("--- inner ---");
                chain.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
                if (e.StackTrace is { } st) chain.AppendLine(st);
            }
            uncaught = chain.ToString();
            await stdErrWriter.WriteLineAsync(uncaught).ConfigureAwait(false);
        }
        finally
        {
            // Drain order: flush the writers first so in-buffer bytes make it to the bridge,
            // then restore Console.{Out,Error,In} to their previous values, then detach the
            // AssemblyResolve handler, then restore the SynchronizationContext. Writers are
            // also disposed by the `using` block, which flushes again defensively. The
            // `_inputStateDisposer` runs last (via `using`) so the BrowserTerminalReader
            // completes (wakes a pending ReadLineAsync with EOF) only after Console.In no
            // longer points at it.
            stdOutWriter.FlushNow();
            stdErrWriter.FlushNow();
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            SetConsoleInField(oldIn);
            // T3 — clear the flag so subsequent non-interactive runs see PNS again.
            AppContext.SetData("Carbide.InteractiveBridge", false);
            AppDomain.CurrentDomain.AssemblyResolve -= resolveHandler;
            SynchronizationContext.SetSynchronizationContext(oldSyncContext);
        }

        sw.Stop();
        var stdOut = stdOutTee.ToString();
        var stdErr = stdErrTee.ToString();

        return uncaught is null
            ? RunResult.Success_(stdOut, stdErr, exitCode, sw.Elapsed.TotalMilliseconds)
            : RunResult.Uncaught(stdOut, stdErr, uncaught, sw.Elapsed.TotalMilliseconds);
    }
}
