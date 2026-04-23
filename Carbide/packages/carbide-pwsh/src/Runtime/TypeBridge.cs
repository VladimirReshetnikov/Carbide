using System.Reflection;
using CarbidePwsh.Errors;

namespace CarbidePwsh.Runtime;

/// <summary>
/// Resolves <c>[Type]</c> expressions and dispatches <c>[Type]::Member</c> and
/// <c>$obj.Member</c> via reflection. Phase 1 makes no attempt to cache method-lookup results
/// — every dispatch walks reflection. Shell-scale workloads don't hit that loop hard enough
/// for it to matter; if profiling surfaces a bottleneck later, a per-type member cache is a
/// ~20-line addition.
/// </summary>
public sealed class TypeBridge
{
    private readonly Dictionary<string, Type> _typeCache = new(StringComparer.OrdinalIgnoreCase);

    public Type ResolveType(string typeName, SourceLocation location)
    {
        if (_typeCache.TryGetValue(typeName, out var cached)) return cached;

        // pwsh-style generics inside a string: `HashSet[string]`,
        // `Dictionary[string,int]`, `List[HashSet[int]]`. Real pwsh parses these at
        // script-lex time; when we receive one as a string (e.g. from `New-Object` with a
        // quoted type name), we parse and recurse to resolve.
        if (TryParsePwshGenericName(typeName, out var baseName, out var genericArgs, out var arrayRank))
        {
            Type closed;
            if (genericArgs.Count > 0)
            {
                // Resolve the open-generic definition: `HashSet` → `HashSet`1`.
                var arity = genericArgs.Count;
                var open = ResolveType($"{baseName}`{arity}", location);
                var args = genericArgs.Select(a => ResolveType(a, location)).ToArray();
                closed = open.MakeGenericType(args);
            }
            else
            {
                closed = ResolveType(baseName, location);
            }
            for (int i = 0; i < arrayRank; i++) closed = closed.MakeArrayType();
            _typeCache[typeName] = closed;
            return closed;
        }

        // Aliases first.
        if (TypeAliases.Aliases.TryGetValue(typeName, out var aliased))
        {
            _typeCache[typeName] = aliased;
            return aliased;
        }

        // Direct `Type.GetType` — handles fully-qualified assembly-qualified names.
        var direct = Type.GetType(typeName, throwOnError: false, ignoreCase: true);
        if (direct != null)
        {
            _typeCache[typeName] = direct;
            return direct;
        }

        // Probe loaded assemblies.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(typeName, throwOnError: false, ignoreCase: true);
            if (t != null)
            {
                _typeCache[typeName] = t;
                return t;
            }
            // Try with "System." prefix as a convenience.
            if (!typeName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
            {
                var tp = asm.GetType("System." + typeName, throwOnError: false, ignoreCase: true);
                if (tp != null)
                {
                    _typeCache[typeName] = tp;
                    return tp;
                }
            }
            // Users commonly type `HashSet[string]` / `List[int]` without qualifying the
            // namespace; reflection accepts arity-suffixed names (`HashSet`1`). Probe the
            // stock generic-collection namespaces when the name contains a backtick.
            if (typeName.Contains('`'))
            {
                foreach (var ns in s_genericProbeNamespaces)
                {
                    var candidate = asm.GetType(ns + "." + typeName, throwOnError: false, ignoreCase: true);
                    if (candidate != null)
                    {
                        _typeCache[typeName] = candidate;
                        return candidate;
                    }
                }
            }
        }

        throw new PwshTypeNotFoundException(typeName, location);
    }

    private static readonly string[] s_genericProbeNamespaces =
    {
        "System.Collections.Generic",
        "System.Collections.Concurrent",
        "System.Collections.ObjectModel",
    };

    /// <summary>
    /// Parse a pwsh-style generic type name (<c>HashSet[string]</c>,
    /// <c>Dictionary[string, List[int]]</c>, <c>List[int][]</c>) into
    /// (base-name, generic-args, array-rank). Returns <see langword="false"/> when the
    /// name has no brackets or the bracket structure doesn't balance.
    /// </summary>
    private static bool TryParsePwshGenericName(string name, out string baseName, out List<string> genericArgs, out int arrayRank)
    {
        baseName = name;
        genericArgs = new List<string>();
        arrayRank = 0;
        int firstBracket = name.IndexOf('[');
        if (firstBracket < 0) return false;
        baseName = name.Substring(0, firstBracket).Trim();
        int i = firstBracket;
        while (i < name.Length)
        {
            if (name[i] != '[') return false;
            if (i + 1 < name.Length && name[i + 1] == ']')
            {
                arrayRank++;
                i += 2;
                continue;
            }
            int depth = 1;
            int argStart = i + 1;
            i++;
            while (i < name.Length && depth > 0)
            {
                if (name[i] == '[') depth++;
                else if (name[i] == ']')
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (name[i] == ',' && depth == 1)
                {
                    genericArgs.Add(name.Substring(argStart, i - argStart).Trim());
                    argStart = i + 1;
                }
                i++;
            }
            if (depth != 0) return false;
            genericArgs.Add(name.Substring(argStart, i - argStart).Trim());
            i++;
        }
        return true;
    }

    // ---------------- Static member access ----------------

    public object? GetStaticMember(Type type, string name, SourceLocation location)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(name, flags);
        if (prop != null) return UnwrapTargetInvocation(() => prop.GetValue(null));
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(null);
        if (name.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            // Expose [Type]::new as a factory.
            return new ConstructorInvoker(type);
        }
        throw NewMemberNotFound(type, name, isStatic: true, location);
    }

    public void SetStaticMember(Type type, string name, object? value, SourceLocation location)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(name, flags);
        if (prop != null && prop.CanWrite)
        {
            var coerced = Coercion.To(value, prop.PropertyType);
            UnwrapTargetInvocation(() => { prop.SetValue(null, coerced); return (object?)null; });
            return;
        }
        var field = type.GetField(name, flags);
        if (field != null && !field.IsInitOnly)
        {
            var coerced = Coercion.To(value, field.FieldType);
            field.SetValue(null, coerced);
            return;
        }
        throw NewMemberNotFound(type, name, isStatic: true, location);
    }

    public object? InvokeStaticMethod(Type type, string name, object?[] args, SourceLocation location)
    {
        if (name.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            return InvokeConstructor(type, args, location);
        }
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy |
                                   BindingFlags.IgnoreCase | BindingFlags.InvokeMethod;
        var methods = type.GetMethods(flags).Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (methods.Length == 0)
            throw NewMemberNotFound(type, name, isStatic: true, location);
        return InvokeBestMatch(methods, null, args, location);
    }

    // ---------------- Instance member access ----------------

    public object? GetInstanceMember(object target, string name, SourceLocation location)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(name, flags);
        if (prop != null && prop.CanRead) return UnwrapTargetInvocation(() => prop.GetValue(target));
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(target);

        // Special cases: dictionaries expose their keys as properties.
        if (target is System.Collections.IDictionary dict)
        {
            if (dict.Contains(name)) return dict[name];
        }

        throw NewMemberNotFound(type, name, isStatic: false, location);
    }

    public void SetInstanceMember(object target, string name, object? value, SourceLocation location)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(name, flags);
        if (prop != null && prop.CanWrite)
        {
            var coerced = Coercion.To(value, prop.PropertyType);
            UnwrapTargetInvocation(() => { prop.SetValue(target, coerced); return (object?)null; });
            return;
        }
        var field = type.GetField(name, flags);
        if (field != null && !field.IsInitOnly)
        {
            var coerced = Coercion.To(value, field.FieldType);
            field.SetValue(target, coerced);
            return;
        }
        if (target is System.Collections.IDictionary dict)
        {
            dict[name] = value;
            return;
        }
        throw NewMemberNotFound(type, name, isStatic: false, location);
    }

    public object? InvokeInstanceMethod(object target, string name, object?[] args, SourceLocation location)
    {
        var type = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy |
                                   BindingFlags.IgnoreCase | BindingFlags.InvokeMethod;
        var methods = type.GetMethods(flags).Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (methods.Length == 0)
            throw NewMemberNotFound(type, name, isStatic: false, location);
        return InvokeBestMatch(methods, target, args, location);
    }

    // ---------------- Overload resolution ----------------

    private object? InvokeBestMatch(MethodInfo[] candidates, object? target, object?[] args, SourceLocation location)
    {
        MethodInfo? best = null;
        object?[]? bestConverted = null;
        int bestScore = int.MinValue;
        var candidateDescriptions = new List<string>();

        foreach (var m in candidates.OrderBy(c => c.MetadataToken))
        {
            var ps = m.GetParameters();
            candidateDescriptions.Add(FormatMethod(m));
            if (ps.Length != args.Length) continue;
            var converted = new object?[ps.Length];
            int score = 0;
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                var arg = args[i];
                if (arg == null)
                {
                    if (pt.IsValueType && Nullable.GetUnderlyingType(pt) == null) { ok = false; break; }
                    converted[i] = null;
                    score += 50;
                    continue;
                }
                if (pt.IsInstanceOfType(arg))
                {
                    converted[i] = arg;
                    score += 100;
                    continue;
                }
                try
                {
                    converted[i] = Coercion.To(arg, pt);
                    score += 10;
                }
                catch
                {
                    ok = false; break;
                }
            }
            if (!ok) continue;
            if (score > bestScore)
            {
                bestScore = score;
                best = m;
                bestConverted = converted;
            }
        }

        if (best == null)
        {
            throw new PwshMethodBindingException(
                $"Cannot find an overload of '{candidates[0].DeclaringType?.Name}.{candidates[0].Name}' " +
                $"that accepts {args.Length} argument(s) of the given types. Candidates:\n  " +
                string.Join("\n  ", candidateDescriptions),
                location);
        }

        return UnwrapTargetInvocation(() => best.Invoke(target, bestConverted));
    }

    private object? InvokeConstructor(Type type, object?[] args, SourceLocation location)
    {
        if (args.Length == 0 && type.IsValueType) return Activator.CreateInstance(type);
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == args.Length).ToArray();
        if (ctors.Length == 0)
            throw new PwshMethodBindingException(
                $"[{type.FullName}] has no public constructor that accepts {args.Length} argument(s).",
                location);

        ConstructorInfo? best = null;
        object?[]? bestConverted = null;
        int bestScore = int.MinValue;
        foreach (var c in ctors.OrderBy(c => c.MetadataToken))
        {
            var ps = c.GetParameters();
            var converted = new object?[ps.Length];
            int score = 0;
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                var arg = args[i];
                if (arg == null)
                {
                    if (pt.IsValueType && Nullable.GetUnderlyingType(pt) == null) { ok = false; break; }
                    converted[i] = null;
                    score += 50; continue;
                }
                if (pt.IsInstanceOfType(arg)) { converted[i] = arg; score += 100; continue; }
                try { converted[i] = Coercion.To(arg, pt); score += 10; }
                catch { ok = false; break; }
            }
            if (!ok) continue;
            if (score > bestScore) { bestScore = score; best = c; bestConverted = converted; }
        }
        if (best == null)
            throw new PwshMethodBindingException(
                $"Cannot find a matching constructor for [{type.FullName}] with the given argument types.",
                location);
        return UnwrapTargetInvocation(() => best.Invoke(bestConverted));
    }

    // ---------------- Indexer ----------------

    public object? GetIndex(object target, object? index, SourceLocation location)
    {
        if (target is System.Collections.IDictionary dict)
        {
            if (index is null) return null;
            return dict[index];
        }
        if (target is string s)
        {
            if (TryGetIndexList(index, out var stringIndices))
            {
                var slice = new List<object?>();
                foreach (var rawIndex in stringIndices)
                {
                    var normalized = NormalizeIndex(rawIndex, s.Length);
                    if (normalized >= 0 && normalized < s.Length)
                        slice.Add(s[normalized]);
                }
                return slice.ToArray();
            }

            var i = (int)Coercion.ToInt64(index);
            if (i < 0) i += s.Length;
            if (i < 0 || i >= s.Length) return null;
            return s[i];
        }
        if (target is Array arr)
        {
            if (TryGetIndexList(index, out var arrayIndices))
            {
                if (arr.Rank > 1 && arrayIndices.Count == arr.Rank)
                {
                    var multidimensional = new int[arrayIndices.Count];
                    for (int axis = 0; axis < arrayIndices.Count; axis++)
                    {
                        multidimensional[axis] = NormalizeIndex(arrayIndices[axis], arr.GetLength(axis));
                        if (multidimensional[axis] < 0 || multidimensional[axis] >= arr.GetLength(axis))
                            return null;
                    }

                    return arr.GetValue(multidimensional);
                }

                var list = new List<object?>();
                foreach (var ind in arrayIndices)
                {
                    var normalized = NormalizeIndex(ind, arr.Length);
                    if (normalized >= 0 && normalized < arr.Length)
                        list.Add(arr.GetValue(normalized));
                }
                return list.ToArray();
            }
            var i = (int)Coercion.ToInt64(index);
            if (i < 0) i += arr.Length;
            if (i < 0 || i >= arr.Length) return null;
            return arr.GetValue(i);
        }
        if (target is System.Collections.IList list2)
        {
            if (TryGetIndexList(index, out var listIndices))
            {
                var slice = new List<object?>();
                foreach (var ind in listIndices)
                {
                    var normalized = NormalizeIndex(ind, list2.Count);
                    if (normalized >= 0 && normalized < list2.Count)
                        slice.Add(list2[normalized]);
                }
                return slice.ToArray();
            }

            var i = (int)Coercion.ToInt64(index);
            if (i < 0) i += list2.Count;
            if (i < 0 || i >= list2.Count) return null;
            return list2[i];
        }
        var type = target.GetType();
        var idxProp = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        if (idxProp != null)
        {
            var ps = idxProp.GetIndexParameters();
            if (ps.Length == 1)
            {
                var coerced = Coercion.To(index, ps[0].ParameterType);
                return UnwrapTargetInvocation(() => idxProp.GetValue(target, new[] { coerced }));
            }
        }
        throw new PwshRuntimeException($"Values of type [{type.FullName}] are not indexable.", location);
    }

    private static bool TryGetIndexList(object? index, out List<object?> indices)
    {
        if (index is Array array)
        {
            indices = array.Cast<object?>().ToList();
            return true;
        }

        if (index is System.Collections.IEnumerable enumerable && index is not string)
        {
            indices = enumerable.Cast<object?>().ToList();
            return true;
        }

        indices = null!;
        return false;
    }

    private static int NormalizeIndex(object? index, int length)
    {
        var normalized = (int)Coercion.ToInt64(index);
        if (normalized < 0)
            normalized += length;
        return normalized;
    }

    // ---------------- Helpers ----------------

    private static string FormatMethod(MethodInfo m)
        => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) : {m.ReturnType.Name}";

    private static PwshMemberNotFoundException NewMemberNotFound(Type type, string name, bool isStatic, SourceLocation location)
    {
        var flags = BindingFlags.Public | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var memberNames = type.GetMembers(flags).Select(m => m.Name).Distinct().ToArray();
        var near = memberNames
            .Select(n => (name: n, dist: Levenshtein(n, name)))
            .OrderBy(p => p.dist).Take(3)
            .Where(p => p.dist <= Math.Max(3, name.Length / 2))
            .Select(p => p.name).ToArray();
        return new PwshMemberNotFoundException(type.FullName ?? type.Name, name, near, location);
    }

    private static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        var ac = a.Length; var bc = b.Length;
        var d = new int[ac + 1, bc + 1];
        for (int i = 0; i <= ac; i++) d[i, 0] = i;
        for (int j = 0; j <= bc; j++) d[0, j] = j;
        for (int i = 1; i <= ac; i++)
        {
            for (int j = 1; j <= bc; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[ac, bc];
    }

    private static object? UnwrapTargetInvocation(Func<object?> action)
    {
        try { return action(); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new PwshRuntimeException(tie.InnerException.Message, SourceLocation.None, tie.InnerException);
        }
    }
}

/// <summary>Marker returned by <c>[Type]::new</c> that invokes a constructor when called.</summary>
public sealed class ConstructorInvoker
{
    public Type Type { get; }
    public ConstructorInvoker(Type type) { Type = type; }
}
