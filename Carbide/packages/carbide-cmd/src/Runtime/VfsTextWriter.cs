using System.Text;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Runtime;

/// <summary>
/// A <see cref="TextWriter"/> that buffers text in memory and materializes it into a VFS file
/// when disposed. cmd's <c>&gt;</c> / <c>&gt;&gt;</c> redirections use this: the interpreter
/// swaps the target stream for an instance of this writer; when the command exits, the writer
/// is disposed and the file appears in the VFS.
/// </summary>
internal sealed class VfsTextWriter : TextWriter
{
    private readonly VirtualFileSystem _vfs;
    private readonly string _path;
    private readonly bool _append;
    private readonly StringBuilder _buffer = new();

    public VfsTextWriter(VirtualFileSystem vfs, string path, bool append)
    {
        _vfs = vfs;
        _path = path;
        _append = append;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => _buffer.Append(value);
    public override void Write(string? value) { if (value != null) _buffer.Append(value); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var abs = _vfs.Normalize(_path);
            var existing = _vfs.Resolve(abs) as VfsFile;
            if (_append && existing is not null)
                existing.AppendText(_buffer.ToString());
            else
                _vfs.CreateTextFile(abs, _buffer.ToString(), overwrite: true);
        }
        base.Dispose(disposing);
    }
}
