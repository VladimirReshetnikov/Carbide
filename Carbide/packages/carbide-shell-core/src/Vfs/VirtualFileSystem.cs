using CarbideShellCore.Errors;

namespace CarbideShellCore.Vfs;

public sealed class VirtualFileSystem
{
    public VfsDirectory Root { get; }
    public string CurrentLocation { get; set; } = VfsPath.RootPath;

    public VirtualFileSystem()
    {
        Root = new VfsDirectory { Name = "" };
    }

    public string Normalize(string path) => VfsPath.Normalize(path, CurrentLocation);

    public VfsNode? Resolve(string path)
    {
        var abs = Normalize(path);
        if (abs == VfsPath.RootPath) return Root;
        var segments = VfsPath.Split(abs);
        VfsNode current = Root;
        foreach (var seg in segments)
        {
            if (current is not VfsDirectory dir) return null;
            if (!dir.Children.TryGetValue(seg, out var next)) return null;
            current = next;
        }
        return current;
    }

    public VfsNode GetRequired(string path)
        => Resolve(path) ?? throw new VfsException($"Cannot find path '{path}' because it does not exist.");

    public bool Exists(string path) => Resolve(path) != null;

    public bool IsDirectory(string path) => Resolve(path) is VfsDirectory;

    public bool IsFile(string path) => Resolve(path) is VfsFile;

    public VfsDirectory GetOrCreateDirectory(string path)
    {
        var abs = Normalize(path);
        if (abs == VfsPath.RootPath) return Root;
        var segments = VfsPath.Split(abs);
        VfsDirectory current = Root;
        foreach (var seg in segments)
        {
            if (!current.Children.TryGetValue(seg, out var next))
            {
                var dir = new VfsDirectory(seg) { Parent = current };
                current.Children[seg] = dir;
                current = dir;
            }
            else if (next is VfsDirectory asDir)
            {
                current = asDir;
            }
            else
            {
                throw new VfsException($"'{current.AbsolutePath}/{seg}' exists and is not a directory.");
            }
        }
        return current;
    }

    public VfsDirectory CreateDirectory(string path)
    {
        var abs = Normalize(path);
        if (Resolve(abs) is VfsDirectory existing) return existing;
        if (Resolve(abs) != null) throw new VfsException($"'{abs}' already exists and is not a directory.");
        var (parentPath, leaf) = VfsPath.SplitLeaf(abs);
        if (leaf.Length == 0) return Root;
        var parent = GetOrCreateDirectory(parentPath);
        var dir = new VfsDirectory(leaf) { Parent = parent };
        parent.Children[leaf] = dir;
        return dir;
    }

    public VfsFile CreateFile(string path, byte[] content, bool overwrite, string encoding = "utf-8")
    {
        var abs = Normalize(path);
        var existing = Resolve(abs);
        if (existing is VfsDirectory)
            throw new VfsException($"'{abs}' is a directory; cannot write as file.");
        if (existing is VfsFile file && !overwrite)
            throw new VfsException($"'{abs}' already exists. Use -Force to overwrite.");
        if (existing is VfsFile existingFile)
        {
            existingFile.Content = content;
            existingFile.Encoding = encoding;
            existingFile.LastWriteTimeUtc = DateTime.UtcNow;
            return existingFile;
        }
        var (parentPath, leaf) = VfsPath.SplitLeaf(abs);
        if (leaf.Length == 0) throw new VfsException("Cannot create a file at the filesystem root.");
        var parent = GetOrCreateDirectory(parentPath);
        var f = new VfsFile(leaf)
        {
            Parent = parent,
            Content = content,
            Encoding = encoding,
        };
        parent.Children[leaf] = f;
        return f;
    }

    public VfsFile CreateTextFile(string path, string content, bool overwrite, string encoding = "utf-8")
    {
        var enc = VfsFile.EncodingFromName(encoding);
        return CreateFile(path, enc.GetBytes(content ?? ""), overwrite, encoding);
    }

    public void Delete(string path, bool recursive, bool force)
    {
        var node = Resolve(path);
        if (node == null)
        {
            if (force) return;
            throw new VfsException($"Cannot find path '{path}' because it does not exist.");
        }
        if (node == Root)
            throw new VfsException("Cannot remove the filesystem root.");
        if (node is VfsDirectory dir && dir.Children.Count > 0 && !recursive)
            throw new VfsException($"Directory '{node.AbsolutePath}' is not empty. Use -Recurse to remove.");
        node.Parent!.Children.Remove(node.Name);
    }

    public void Copy(string source, string destination, bool recursive)
    {
        var src = Resolve(source) ?? throw new VfsException($"Cannot find source '{source}'.");
        var dstNormalized = Normalize(destination);
        var dstExisting = Resolve(dstNormalized);
        switch (src)
        {
            case VfsFile f:
                CopyFileInto(f, dstNormalized, dstExisting);
                break;
            case VfsDirectory d:
                if (!recursive) throw new VfsException("Use -Recurse to copy a directory.");
                CopyDirectoryInto(d, dstNormalized, dstExisting);
                break;
        }
    }

    private void CopyFileInto(VfsFile src, string dstNormalized, VfsNode? dstExisting)
    {
        if (dstExisting is VfsDirectory destDir)
        {
            var newFile = new VfsFile(src.Name)
            {
                Content = (byte[])src.Content.Clone(),
                Encoding = src.Encoding,
                Parent = destDir,
            };
            destDir.Children[src.Name] = newFile;
            return;
        }
        CreateFile(dstNormalized, (byte[])src.Content.Clone(), overwrite: true, encoding: src.Encoding);
    }

    private void CopyDirectoryInto(VfsDirectory src, string dstNormalized, VfsNode? dstExisting)
    {
        VfsDirectory target;
        if (dstExisting is VfsDirectory existingDir)
        {
            target = (VfsDirectory)GetOrCreateDirectory(dstNormalized + "/" + src.Name);
        }
        else
        {
            target = CreateDirectory(dstNormalized);
        }
        foreach (var child in src.Children.Values.ToList())
        {
            var childDestination = target.AbsolutePath + "/" + child.Name;
            switch (child)
            {
                case VfsFile cf:
                    CreateFile(childDestination, (byte[])cf.Content.Clone(), overwrite: true, encoding: cf.Encoding);
                    break;
                case VfsDirectory cd:
                    var subTarget = CreateDirectory(childDestination);
                    CopyDirectoryInto(cd, subTarget.AbsolutePath, subTarget);
                    break;
            }
        }
    }

    public void Move(string source, string destination)
    {
        var src = Resolve(source) ?? throw new VfsException($"Cannot find source '{source}'.");
        var dstNormalized = Normalize(destination);
        var dstExisting = Resolve(dstNormalized);
        if (dstExisting is VfsDirectory destDir)
        {
            src.Parent!.Children.Remove(src.Name);
            destDir.Children[src.Name] = src;
            src.Parent = destDir;
            return;
        }
        var (parentPath, leaf) = VfsPath.SplitLeaf(dstNormalized);
        if (leaf.Length == 0) throw new VfsException("Cannot move to filesystem root.");
        var parent = GetOrCreateDirectory(parentPath);
        src.Parent!.Children.Remove(src.Name);
        src.Name = leaf;
        parent.Children[leaf] = src;
        src.Parent = parent;
    }

    public IEnumerable<VfsNode> List(string path, bool recursive, string? filter, bool filesOnly = false, bool directoriesOnly = false)
    {
        var node = Resolve(path) ?? throw new VfsException($"Cannot find path '{path}'.");
        if (node is VfsFile f)
        {
            yield return f;
            yield break;
        }
        var dir = (VfsDirectory)node;
        foreach (var child in EnumerateChildren(dir, recursive))
        {
            if (filter != null && !FilterMatch(child.Name, filter)) continue;
            if (filesOnly && !child.IsFile) continue;
            if (directoriesOnly && !child.IsDirectory) continue;
            yield return child;
        }
    }

    private static IEnumerable<VfsNode> EnumerateChildren(VfsDirectory dir, bool recursive)
    {
        foreach (var c in dir.Children.Values)
        {
            yield return c;
            if (recursive && c is VfsDirectory sub)
                foreach (var grand in EnumerateChildren(sub, recursive))
                    yield return grand;
        }
    }

    private static bool FilterMatch(string name, string filter)
    {
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(filter)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public void SetLocation(string path)
    {
        var abs = Normalize(path);
        var node = Resolve(abs);
        if (node is not VfsDirectory)
            throw new VfsException($"'{abs}' is not a directory.");
        CurrentLocation = abs;
    }
}
