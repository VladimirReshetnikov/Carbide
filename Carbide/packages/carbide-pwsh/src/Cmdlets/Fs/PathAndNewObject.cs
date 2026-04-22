using CarbidePwsh.Errors;
using CarbidePwsh.Runtime;
using CarbideShellCore.Vfs;

namespace CarbidePwsh.Cmdlets.Fs;

/// <summary>
/// <c>Split-Path</c> — pwsh's path-surgery cmdlet. The common shapes are:
/// <list type="bullet">
///   <item><c>Split-Path /a/b/c.txt</c> → <c>/a/b</c> (parent, default)</item>
///   <item><c>Split-Path -Parent /a/b/c.txt</c> → <c>/a/b</c></item>
///   <item><c>Split-Path -Leaf /a/b/c.txt</c> → <c>c.txt</c></item>
///   <item><c>Split-Path -Extension /a/b/c.txt</c> → <c>.txt</c></item>
///   <item><c>Split-Path -LeafBase /a/b/c.txt</c> → <c>c</c></item>
/// </list>
/// </summary>
public sealed class SplitPathCommand : Cmdlet
{
    public override string Name => "Split-Path";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Split-Path requires a -Path argument.");
        var (_, leaf) = VfsPath.SplitLeaf(VfsPath.Normalize(path, "/"));
        var parentPath = VfsPath.SplitLeaf(VfsPath.Normalize(path, "/")).Parent;
        if (binding.HasSwitch("Leaf")) { yield return leaf; yield break; }
        if (binding.HasSwitch("Extension"))
        {
            var ext = VfsPath.GetExtension(path);
            yield return ext;
            yield break;
        }
        if (binding.HasSwitch("LeafBase"))
        {
            var dot = leaf.LastIndexOf('.');
            yield return dot > 0 ? leaf.Substring(0, dot) : leaf;
            yield break;
        }
        if (binding.HasSwitch("IsAbsolute"))
        {
            yield return VfsPath.IsAbsolute(path);
            yield break;
        }
        // Default: -Parent.
        yield return parentPath;
    }
}

/// <summary>
/// <c>New-Object</c> — construct an instance of a BCL type via reflection. Supported
/// shapes: <c>New-Object TypeName</c> (no-arg constructor) and
/// <c>New-Object TypeName -ArgumentList arg1, arg2</c>. The type name may include a
/// fully-qualified namespace (<c>System.Collections.Generic.List[string]</c>
/// is Phase-2 stretch; Phase 1 takes a plain <c>TypeName</c>).
/// </summary>
public sealed class NewObjectCommand : Cmdlet
{
    public override string Name => "New-Object";

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var typeName = binding.GetValue<string>("TypeName", 0, null)
            ?? throw new PwshRuntimeException("New-Object requires a -TypeName argument.");
        var argList = binding.GetOrDefault<object?>("ArgumentList", null);
        var args = argList switch
        {
            null => Array.Empty<object?>(),
            object[] arr => arr,
            System.Collections.IEnumerable e when argList is not string => e.Cast<object?>().ToArray(),
            _ => new[] { argList },
        };
        var type = context.Types.ResolveType(typeName, Errors.SourceLocation.None);
        yield return context.Types.InvokeStaticMethod(type, "new", args, Errors.SourceLocation.None);
    }
}

/// <summary>
/// <c>Rename-Item</c> — rename a VFS entry. Thin wrapper over <c>VirtualFileSystem.Move</c>
/// that preserves the parent directory and applies only the new leaf name.
/// </summary>
public sealed class RenameItemCommand : Cmdlet
{
    public override string Name => "Rename-Item";
    public override IEnumerable<string> Aliases => new[] { "ren", "rni" };

    public override IEnumerable<object?> Invoke(IEnumerable<object?>? input, ParameterBinding binding, CmdletContext context)
    {
        var path = binding.GetValue<string>("Path", 0, null)
            ?? throw new PwshRuntimeException("Rename-Item requires a -Path argument.");
        var newName = binding.GetValue<string>("NewName", 1, null)
            ?? throw new PwshRuntimeException("Rename-Item requires a -NewName argument.");
        var abs = context.Vfs.Normalize(path);
        var (parent, _) = VfsPath.SplitLeaf(abs);
        var dest = parent == "/" ? "/" + newName : parent + "/" + newName;
        context.Vfs.Move(abs, dest);
        yield break;
    }
}
