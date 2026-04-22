using System.Text;

namespace CarbideShellCore.Vfs;

public abstract class VfsNode
{
    public string Name { get; internal set; } = "";
    public VfsDirectory? Parent { get; internal set; }
    public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastWriteTimeUtc { get; set; } = DateTime.UtcNow;

    public string AbsolutePath
    {
        get
        {
            if (Parent == null) return VfsPath.RootPath;
            if (Parent.Parent == null) return "/" + Name;
            return Parent.AbsolutePath + "/" + Name;
        }
    }

    public bool IsDirectory => this is VfsDirectory;
    public bool IsFile => this is VfsFile;

    /// <summary>
    /// Display mode analogous to <c>Get-ChildItem</c>'s first column. The string is
    /// pwsh-flavored by default because the pwsh shell is the oldest consumer; cmd and bash
    /// shells format listings with their own presenters and do not consult this property.
    /// </summary>
    public virtual string Mode => IsDirectory ? "d----" : "-a---";
}

public sealed class VfsDirectory : VfsNode
{
    public IDictionary<string, VfsNode> Children { get; } =
        new SortedDictionary<string, VfsNode>(StringComparer.OrdinalIgnoreCase);

    public VfsDirectory() { }
    public VfsDirectory(string name) { Name = name; }
}

public sealed class VfsFile : VfsNode
{
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string Encoding { get; set; } = "utf-8";
    public long Length => Content.Length;

    public VfsFile() { }
    public VfsFile(string name) { Name = name; }

    public string ReadText()
    {
        var enc = EncodingFromName(Encoding);
        return enc.GetString(Content);
    }

    public void WriteText(string text)
    {
        var enc = EncodingFromName(Encoding);
        Content = enc.GetBytes(text ?? "");
        LastWriteTimeUtc = DateTime.UtcNow;
    }

    public void AppendText(string text)
    {
        WriteText(ReadText() + (text ?? ""));
    }

    public static System.Text.Encoding EncodingFromName(string name) => (name ?? "utf-8").ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "utf-16" or "utf16" or "unicode" => System.Text.Encoding.Unicode,
        "ascii" => System.Text.Encoding.ASCII,
        "utf-8-bom" or "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        _ => System.Text.Encoding.UTF8,
    };
}
