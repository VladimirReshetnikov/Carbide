// Adapted from WasmSharp.Core.Services.WasmMetadataReferenceResolver.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).
// Extended: pre-validates PE managed-metadata presence so trimmed facade DLLs (≈5 KB, no
// metadata) don't surface as CS0009 during user compilation.

using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Carbide.Core.Services;

public class WasmMetadataReferenceResolver
{
    private static readonly HttpClient Client = new();

#pragma warning disable CA1822 // Mark members as static
    public async Task<MetadataReference?> ResolveReferenceAsync(string assembly)
#pragma warning restore CA1822 // Mark members as static
    {
        var (_, reference) = await ResolveWithBytesAsync(assembly).ConfigureAwait(false);
        return reference;
    }

#pragma warning disable CA1822 // Mark members as static
    public async Task<(byte[] bytes, MetadataReference? reference)> ResolveWithBytesAsync(string assembly)
#pragma warning restore CA1822 // Mark members as static
    {
        var url = new Uri(assembly, UriKind.Absolute);
        // Avoid EnsureSuccessStatusCode: Mono-WASM's file:// fetch shim returns ok=true with a
        // status of 0, which HttpClient treats as a non-success code.
        var response = await Client.GetAsync(url).ConfigureAwait(false);

        // Copy the stream fully into memory so the underlying Mono-WASM fetch shim (which
        // surfaces content-length as 0 from file:// responses) doesn't truncate reads.
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms).ConfigureAwait(false);
        var bytes = ms.ToArray();

        if (!HasManagedMetadata(bytes))
        {
            return (bytes, null);
        }
        var reference = MetadataReference.CreateFromImage(bytes, new(MetadataImageKind.Assembly), filePath: url.AbsolutePath);
        return (bytes, reference);
    }

    internal static bool HasManagedMetadata(byte[] bytes)
    {
        if (bytes.Length < 64)
        {
            return false;
        }
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var reader = new PEReader(ms);
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
