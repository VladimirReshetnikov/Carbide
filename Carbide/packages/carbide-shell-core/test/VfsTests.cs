using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;
using Xunit;

namespace CarbideShellCore.Tests;

public class VfsTests
{
    [Fact]
    public void RootExists()
    {
        var vfs = new VirtualFileSystem();
        Assert.True(vfs.Exists("/"));
        Assert.True(vfs.IsDirectory("/"));
    }

    [Fact]
    public void CreateDirectoryIsRecursive()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateDirectory("/a/b/c");
        Assert.True(vfs.IsDirectory("/a"));
        Assert.True(vfs.IsDirectory("/a/b"));
        Assert.True(vfs.IsDirectory("/a/b/c"));
    }

    [Fact]
    public void CreateAndReadFile()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/work/hello.txt", "hello world", overwrite: false);
        var node = vfs.Resolve("/work/hello.txt");
        var file = Assert.IsType<VfsFile>(node);
        Assert.Equal("hello world", file.ReadText());
    }

    [Fact]
    public void OverwriteRequiresForce()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/foo.txt", "1", overwrite: false);
        Assert.Throws<VfsException>(() => vfs.CreateTextFile("/foo.txt", "2", overwrite: false));
        vfs.CreateTextFile("/foo.txt", "2", overwrite: true);
        Assert.Equal("2", ((VfsFile)vfs.Resolve("/foo.txt")!).ReadText());
    }

    [Fact]
    public void DeleteFile()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/f.txt", "x", overwrite: false);
        vfs.Delete("/f.txt", recursive: false, force: false);
        Assert.False(vfs.Exists("/f.txt"));
    }

    [Fact]
    public void DeleteNonEmptyRequiresRecurse()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/work/f.txt", "x", overwrite: false);
        Assert.Throws<VfsException>(() => vfs.Delete("/work", recursive: false, force: false));
        vfs.Delete("/work", recursive: true, force: false);
        Assert.False(vfs.Exists("/work"));
    }

    [Theory]
    [InlineData("/tmp", "/tmp")]
    [InlineData("tmp", "/tmp")]
    [InlineData("./sub", "/sub")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("~", "/home/user")]
    [InlineData("~/docs", "/home/user/docs")]
    [InlineData("", "/")]
    public void NormalizePath(string input, string expected)
    {
        Assert.Equal(expected, VfsPath.Normalize(input, "/"));
    }

    [Fact]
    public void NormalizeRelativeToLocation()
    {
        Assert.Equal("/work/sub", VfsPath.Normalize("sub", "/work"));
    }

    [Fact]
    public void SnapshotRoundTripPreservesStructure()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateDirectory("/work/sub");
        vfs.CreateTextFile("/work/a.txt", "one", overwrite: false);
        vfs.CreateTextFile("/work/sub/b.txt", "two", overwrite: false);
        vfs.CurrentLocation = "/work";

        var json = VfsSnapshot.Save(vfs);
        var vfs2 = new VirtualFileSystem();
        VfsSnapshot.Load(vfs2, json);

        Assert.True(vfs2.IsDirectory("/work"));
        Assert.True(vfs2.IsDirectory("/work/sub"));
        Assert.Equal("one", ((VfsFile)vfs2.Resolve("/work/a.txt")!).ReadText());
        Assert.Equal("two", ((VfsFile)vfs2.Resolve("/work/sub/b.txt")!).ReadText());
        Assert.Equal("/work", vfs2.CurrentLocation);
    }

    [Fact]
    public void ListNonRecursive()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/a.txt", "", overwrite: false);
        vfs.CreateTextFile("/b.txt", "", overwrite: false);
        vfs.CreateDirectory("/sub");
        var names = vfs.List("/", recursive: false, filter: null).Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "a.txt", "b.txt", "sub" }, names);
    }

    [Fact]
    public void ListRecursive()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/a.txt", "", overwrite: false);
        vfs.CreateTextFile("/sub/b.txt", "", overwrite: false);
        var names = vfs.List("/", recursive: true, filter: null).Select(n => n.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "a.txt", "b.txt", "sub" }, names);
    }

    [Fact]
    public void FilterMatchesGlob()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/a.json", "", overwrite: false);
        vfs.CreateTextFile("/b.txt", "", overwrite: false);
        var names = vfs.List("/", recursive: false, filter: "*.json").Select(n => n.Name).ToArray();
        Assert.Equal(new[] { "a.json" }, names);
    }

    [Fact]
    public void MoveRenamesFile()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/a.txt", "x", overwrite: false);
        vfs.Move("/a.txt", "/b.txt");
        Assert.False(vfs.Exists("/a.txt"));
        Assert.Equal("x", ((VfsFile)vfs.Resolve("/b.txt")!).ReadText());
    }

    [Fact]
    public void CopyPreservesContent()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/a.txt", "hello", overwrite: false);
        vfs.Copy("/a.txt", "/b.txt", recursive: false);
        Assert.Equal("hello", ((VfsFile)vfs.Resolve("/b.txt")!).ReadText());
        Assert.True(vfs.Exists("/a.txt"));
    }

    [Fact]
    public void SetLocationValidatesDirectory()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateTextFile("/file.txt", "", overwrite: false);
        Assert.Throws<VfsException>(() => vfs.SetLocation("/file.txt"));
    }

    [Fact]
    public void SetLocationUpdatesCurrentLocation()
    {
        var vfs = new VirtualFileSystem();
        vfs.CreateDirectory("/work");
        vfs.SetLocation("/work");
        Assert.Equal("/work", vfs.CurrentLocation);
    }

    [Theory]
    [InlineData("/a/b/c.txt", ".txt")]
    [InlineData("/a/b/noext", "")]
    [InlineData("/a.tar.gz", ".gz")]
    [InlineData("/.hidden", "")]
    [InlineData("/", "")]
    public void GetExtensionParsesLeaf(string path, string expected)
    {
        Assert.Equal(expected, VfsPath.GetExtension(path));
    }
}
