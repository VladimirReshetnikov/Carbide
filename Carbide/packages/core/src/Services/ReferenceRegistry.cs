// M3 session-scoped reference registry. Maps server-assigned GUID ids to MetadataReference
// objects backed by caller-supplied PE bytes. See carbide-M3-detailed-plan §3.1 and §5 D25-D30.

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Carbide.Core.Services;

internal sealed class ReferenceRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredReference> _refs =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Validates PE metadata and registers the reference. Returns a new GUID id (see §5 D25).
    /// Bytes are retained so that <see cref="ProjectCompiler.RunAsync"/> can feed them to
    /// <see cref="System.Reflection.Assembly.Load(byte[])"/> right before invoking the user's
    /// entry point — otherwise the runtime can't resolve the reference at load time.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="bytes"/> does not contain a managed PE metadata directory.
    /// </exception>
    public string Add(byte[] bytes, string? name)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        if (!WasmMetadataReferenceResolver.HasManagedMetadata(bytes))
        {
            throw new InvalidOperationException(
                $"Reference '{name ?? "<unnamed>"}' ({bytes.Length} bytes) is not a valid managed PE image. " +
                "Carbide refuses references without a CLR metadata directory to avoid surfacing CS0009 " +
                "diagnostics later at compile time.");
        }

        var id = Guid.NewGuid().ToString("N");
        var reference = MetadataReference.CreateFromImage(
            bytes,
            new MetadataReferenceProperties(MetadataImageKind.Assembly),
            filePath: name ?? id);
        _refs[id] = new RegisteredReference(id, name, bytes, reference);
        return id;
    }

    /// <summary>
    /// Removes the reference with the given id. Returns <c>true</c> if found.
    /// </summary>
    public bool Remove(string id) => _refs.TryRemove(id, out _);

    public bool TryGet(string id, out MetadataReference reference)
    {
        if (_refs.TryGetValue(id, out var entry))
        {
            reference = entry.Reference;
            return true;
        }
        reference = null!;
        return false;
    }

    public bool TryGetBytes(string id, out byte[] bytes)
    {
        if (_refs.TryGetValue(id, out var entry))
        {
            bytes = entry.Bytes;
            return true;
        }
        bytes = null!;
        return false;
    }

    public bool Contains(string id) => _refs.ContainsKey(id);

    /// <summary>Enumerates all registered references, in no particular order.</summary>
    public IEnumerable<MetadataReference> All =>
        _refs.Values.Select(static e => e.Reference);

    private sealed record RegisteredReference(string Id, string? Name, byte[] Bytes, MetadataReference Reference);
}
