// Adapted from WasmSharp.Core.Services.MetadataReferenceResolver.
// Upstream: https://github.com/JakeYallop/WasmSharp (Apache-2.0).

using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Carbide.Core.Services;

public abstract class MetadataReferenceResolver
{
    public abstract Task<MetadataReference> ResolveReferenceAsync(Assembly assembly);
}
