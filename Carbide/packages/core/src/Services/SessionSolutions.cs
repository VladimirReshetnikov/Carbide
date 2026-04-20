// Adapted from WasmSharp.Core.Services.WasmSolution. Expanded with an explicit Session layer
// to make room for multi-project sessions later (architecture §3.1, §14). M1 keeps it
// single-project-per-session in practice.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Collections.Concurrent;
using Carbide.Terminal;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Carbide.Core.Services;

internal sealed class SessionSolutions(ILogger<SessionSolutions> logger)
{
    private readonly ILogger<SessionSolutions> _logger = logger;
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ProjectEntry> _projects = new(StringComparer.Ordinal);
    private bool _referencesLoaded;

    public async Task InitializeReferencesAsync(string[] assemblyUrls)
    {
        if (_referencesLoaded)
        {
            return;
        }

        var resolver = new WasmMetadataReferenceResolver();
        var tasks = new List<Task<(byte[] bytes, MetadataReference? reference)>>(assemblyUrls.Length);
        foreach (var url in assemblyUrls)
        {
            tasks.Add(resolver.ResolveWithBytesAsync(url));
        }

        var resolved = await Task.WhenAll(tasks).ConfigureAwait(false);
        int added = 0;
        int skipped = 0;
        for (int i = 0; i < resolved.Length; i++)
        {
            var (bytes, reference) = resolved[i];
            if (reference is null)
            {
                skipped++;
                _logger.LogInformation(
                    "Skipped '{Url}' — no managed metadata (length={Length} bytes).",
                    assemblyUrls[i], bytes.Length);
                continue;
            }
            MetadataReferenceCache.AddReference(reference);
            added++;
        }

        _referencesLoaded = true;
        _logger.LogInformation("Loaded {Count} metadata references (skipped {Skipped} without managed metadata).", added, skipped);
    }

    public string CreateSession()
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = new SessionState();
        return id;
    }

    public void DisposeSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
        {
            return;
        }

        foreach (var projectId in state.ProjectIds)
        {
            _projects.TryRemove(projectId, out _);
        }
    }

    public string CreateProject(string sessionId, DocumentOptions? options = null, string? assemblyName = null)
    {
        var state = GetSession(sessionId);
        var projectId = Guid.NewGuid().ToString("N");
        var compiler = new ProjectCompiler(state.ReferenceRegistry, assemblyName, options);
        _projects[projectId] = new ProjectEntry(sessionId, compiler);
        state.ProjectIds.Add(projectId);
        return projectId;
    }

    public void AddSource(string projectId, string path, string code)
        => GetProject(projectId).AddSource(path, code);

    public void UpdateSource(string projectId, string path, string code)
        => GetProject(projectId).UpdateSource(path, code);

    public void RemoveSource(string projectId, string path)
        => GetProject(projectId).RemoveSource(path);

    // --- M3: reference registry surface -------------------------------------------------

    /// <summary>Registers PE bytes in the session's registry and returns the new id.</summary>
    public string AddReference(string sessionId, byte[] bytes, string? name)
    {
        var state = GetSession(sessionId);
        return state.ReferenceRegistry.Add(bytes, name);
    }

    /// <summary>
    /// Removes a reference from the session registry. For every project in the session that
    /// has this id attached, rebuilds the metadata-refs list so stale references don't linger.
    /// No-op if the id was never registered.
    /// </summary>
    public bool RemoveReference(string sessionId, string referenceId)
    {
        var state = GetSession(sessionId);
        if (!state.ReferenceRegistry.Remove(referenceId))
        {
            return false;
        }
        foreach (var projectId in state.ProjectIds)
        {
            if (_projects.TryGetValue(projectId, out var entry))
            {
                entry.Compiler.DetachReference(referenceId);
            }
        }
        return true;
    }

    /// <summary>Attaches a session-registered reference to a project.</summary>
    public void AttachReference(string projectId, string referenceId)
    {
        if (!_projects.TryGetValue(projectId, out var entry))
        {
            throw new InvalidOperationException($"Unknown project id '{projectId}'.");
        }
        if (!_sessions.TryGetValue(entry.SessionId, out var state))
        {
            throw new InvalidOperationException(
                $"Project '{projectId}' is attached to session '{entry.SessionId}', which no longer exists.");
        }
        if (!state.ReferenceRegistry.Contains(referenceId))
        {
            throw new InvalidOperationException(
                $"Reference '{referenceId}' is not registered in session '{entry.SessionId}'.");
        }
        entry.Compiler.AttachReference(referenceId);
    }

    // --- build/run ----------------------------------------------------------------------

    public Task<Diagnostic[]> GetDiagnosticsAsync(string projectId)
        => GetProject(projectId).GetDiagnosticsAsync();

    public Task<BuildResult> BuildAsync(string projectId)
        => GetProject(projectId).BuildAsync();

    public Task<RunResult> RunAsync(string projectId, string[]? args = null, string? stdin = null)
        => GetProject(projectId).RunAsync(args ?? Array.Empty<string>(), stdin);

    /// <summary>
    /// T1 — interactive run. Installs streaming stdout/stderr writers that push buffered
    /// chunks to the JS terminal bridge (<c>globalThis.Carbide.Terminal.{write,writeErr}</c>)
    /// during execution, then drains and restores on exit. T2 extends the same entry point
    /// to install a <see cref="Carbide.Terminal.BrowserTerminalReader"/> into
    /// <c>Console.In</c> and bind a <see cref="Carbide.Terminal.TerminalInputState"/> keyed
    /// by <paramref name="projectId"/> so JSExports can route DeliverStdIn / NotifyResize /
    /// DeliverSignal calls to this run.
    /// </summary>
    public Task<RunResult> RunInteractiveAsync(string projectId, InteractiveOptions options)
        => GetProject(projectId).RunInteractiveAsync(projectId, options);

    /// <summary>
    /// T1 — teardown stub. No-op in T1 because <see cref="ProjectCompiler.RunInteractiveAsync"/>
    /// owns the drain+restore inside its own finally block. T2 extends this to unblock
    /// pending async reads and tear down input-side state.
    /// </summary>
    public void DisposeInteractive(string projectId)
    {
        _ = projectId;
        // T1 no-op; kept as a JSExport target so the TS side can signal teardown even
        // though the current implementation doesn't need to act on it.
    }

    private ProjectCompiler GetProject(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var entry))
        {
            throw new InvalidOperationException($"Unknown project id '{projectId}'.");
        }
        return entry.Compiler;
    }

    private SessionState GetSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new InvalidOperationException($"Unknown session id '{sessionId}'.");
        }
        return state;
    }

    private sealed class SessionState
    {
        public HashSet<string> ProjectIds { get; } = new(StringComparer.Ordinal);
        public ReferenceRegistry ReferenceRegistry { get; } = new();
    }

    private sealed record ProjectEntry(string SessionId, ProjectCompiler Compiler);
}
