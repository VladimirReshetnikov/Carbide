using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Carbide.Core.Services;

internal sealed class RunAssemblyRequest
{
    public required byte[] PeBytes { get; init; }
    public IReadOnlyList<byte[]> References { get; init; } = Array.Empty<byte[]>();
    public string[] Args { get; init; } = Array.Empty<string>();
    public string? Stdin { get; init; }
    public string? ContextName { get; init; }
}

internal static class AssemblyRunner
{
    public static async Task<RunResult> RunAsync(RunAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PeBytes.Length == 0)
        {
            return RunResult.Uncaught(
                string.Empty,
                "Invalid executable assembly: PE bytes are empty.",
                "Invalid executable assembly: PE bytes are empty.",
                0);
        }

        var sw = Stopwatch.StartNew();
        var runContext = new AssemblyLoadContext(
            name: request.ContextName ?? $"CarbideRunAssembly-{Guid.NewGuid():N}",
            isCollectible: true);

        var loadedReferences = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var refBytes in request.References)
        {
            if (refBytes.Length == 0) continue;
            try
            {
                var refAssembly = LoadAssembly(runContext, refBytes);
                var simpleName = refAssembly.GetName().Name;
                if (!string.IsNullOrEmpty(simpleName))
                {
                    loadedReferences[simpleName] = refAssembly;
                }
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
            {
                // A neighboring file may be a native or otherwise non-managed payload. The
                // primary executable's Resolving handler only needs managed assemblies.
            }
        }

        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolveHandler = (_, name) =>
        {
            var simpleName = name.Name;
            return simpleName is not null && loadedReferences.TryGetValue(simpleName, out var found)
                ? found
                : null;
        };

        runContext.Resolving += resolveHandler;
        using var stdOutCapture = new StringWriter();
        using var stdErrCapture = new StringWriter();

        int exitCode = 0;
        string? uncaught = null;

        try
        {
            try
            {
                var assembly = LoadAssembly(runContext, request.PeBytes);
                var declaredEntry = assembly.EntryPoint
                    ?? throw new InvalidOperationException("No entry point was found in the executable assembly.");
                var reflectedEntry = ResolveAsyncEntryOrFallback(declaredEntry);

                var oldOut = Console.Out;
                var oldError = Console.Error;
                var oldIn = GetConsoleInField();
                Console.SetOut(stdOutCapture);
                Console.SetError(stdErrCapture);
                if (request.Stdin is not null)
                {
                    SetConsoleInField(new StringReader(request.Stdin));
                }

                try
                {
                    var parameters = reflectedEntry.GetParameters();
                    var invocationArgs = parameters.Length == 0
                        ? Array.Empty<object?>()
                        : new object?[] { request.Args };

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
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldError);
                    SetConsoleInField(oldIn);
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                uncaught = ex.ToString();
                await stdErrCapture.WriteLineAsync(uncaught).ConfigureAwait(false);
            }
        }
        finally
        {
            runContext.Resolving -= resolveHandler;
            runContext.Unload();
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
        Justification = "The in-memory assembly is user code loaded explicitly for reflection-based execution.")]
    private static Assembly LoadAssembly(AssemblyLoadContext context, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return context.LoadFromStream(stream);
    }

    private static FieldInfo? s_cachedConsoleInField;

    private static FieldInfo? ResolveConsoleInField()
    {
        if (s_cachedConsoleInField is not null) return s_cachedConsoleInField;
        var t = typeof(Console);
        var field = t.GetField("s_in", BindingFlags.Static | BindingFlags.NonPublic)
            ?? t.GetField("_in", BindingFlags.Static | BindingFlags.NonPublic);
        s_cachedConsoleInField = field;
        return field;
    }

    private static TextReader? GetConsoleInField()
    {
        var field = ResolveConsoleInField();
        return field?.GetValue(null) as TextReader;
    }

    private static void SetConsoleInField(TextReader? value)
    {
        var field = ResolveConsoleInField();
        field?.SetValue(null, value);
    }

    private static MethodInfo ResolveAsyncEntryOrFallback(MethodInfo declared)
    {
        if (IsAwaitableReturn(declared.ReturnType))
        {
            return declared;
        }

        var declaringType = declared.DeclaringType;
        if (declaringType is null)
        {
            return declared;
        }

        var declaredParams = declared.GetParameters();
        var candidates = declaringType
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => IsAwaitableReturn(m.ReturnType))
            .Where(m => ParametersMatch(m.GetParameters(), declaredParams))
            .ToList();

        if (candidates.Count == 0)
        {
            return declared;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var userDefined = candidates.FirstOrDefault(m => !m.Name.Contains('<', StringComparison.Ordinal));
        return userDefined ?? candidates[0];
    }

    private static bool IsAwaitableReturn(Type t)
    {
        if (t == typeof(Task) || t == typeof(ValueTask)) return true;
        if (!t.IsGenericType) return false;
        var gtd = t.GetGenericTypeDefinition();
        return gtd == typeof(Task<>) || gtd == typeof(ValueTask<>);
    }

    private static bool ParametersMatch(ParameterInfo[] a, ParameterInfo[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].ParameterType != b[i].ParameterType) return false;
        }
        return true;
    }
}
