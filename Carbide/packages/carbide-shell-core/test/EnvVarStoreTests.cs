using CarbideShellCore.Env;
using Xunit;

namespace CarbideShellCore.Tests;

public class EnvVarStoreTests
{
    [Fact]
    public void SetAndGet()
    {
        var env = new EnvVarStore();
        env.Set("FOO", "bar");
        Assert.Equal("bar", env.Get("FOO"));
    }

    [Fact]
    public void NameLookupIsCaseInsensitive()
    {
        var env = new EnvVarStore();
        env.Set("path", "/usr/bin");
        Assert.Equal("/usr/bin", env.Get("PATH"));
        Assert.Equal("/usr/bin", env.Get("Path"));
    }

    [Fact]
    public void SetNullRemoves()
    {
        var env = new EnvVarStore();
        env.Set("X", "1");
        env.Set("X", null);
        Assert.Null(env.Get("X"));
    }

    [Fact]
    public void UnsetRemovesFromInnermostDefiningScope()
    {
        var env = new EnvVarStore();
        env.Set("X", "outer");
        using (env.PushScope())
        {
            env.Set("X", "inner");
            env.Unset("X");
            // The inner copy is gone; the outer value remains through the snapshot that the
            // inner scope held, so "outer" still wins in the merged view. This matches
            // SETLOCAL behavior in cmd.
            Assert.Equal("outer", env.Get("X"));
        }
        Assert.Equal("outer", env.Get("X"));
    }

    [Fact]
    public void PushScopeIsolatesMutations()
    {
        var env = new EnvVarStore();
        env.Set("FOO", "outer");
        using (env.PushScope())
        {
            env.Set("FOO", "inner");
            Assert.Equal("inner", env.Get("FOO"));
        }
        Assert.Equal("outer", env.Get("FOO"));
    }

    [Fact]
    public void PushScopeInheritsOuterValues()
    {
        var env = new EnvVarStore();
        env.Set("FOO", "outer");
        using (env.PushScope())
        {
            Assert.Equal("outer", env.Get("FOO"));
        }
    }

    [Fact]
    public void AllIncludesAllFrames()
    {
        var env = new EnvVarStore();
        env.Set("OUTER", "1");
        using (env.PushScope())
        {
            env.Set("INNER", "2");
            var all = env.All;
            Assert.True(all.ContainsKey("OUTER"));
            Assert.True(all.ContainsKey("INNER"));
        }
    }
}
