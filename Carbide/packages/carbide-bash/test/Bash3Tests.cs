using Xunit;

namespace CarbideBash.Tests;

/// <summary>
/// Phase-3 bash coverage: heredocs, here-strings, brace expansion, VFS globbing.
/// </summary>
public class Bash3Tests
{
    [Fact]
    public void HeredocFeedsStdin()
    {
        var h = new BashHarness();
        h.Submit("cat <<EOF\none\ntwo\nthree\nEOF\n");
        Assert.Contains("one", h.Output, StringComparison.Ordinal);
        Assert.Contains("two", h.Output, StringComparison.Ordinal);
        Assert.Contains("three", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void HeredocInterpolatesVars()
    {
        var h = new BashHarness();
        h.Submit("NAME=world\ncat <<EOF\nhello, $NAME\nEOF\n");
        Assert.Contains("hello, world", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void HeredocQuotedDelimiterSkipsExpansion()
    {
        var h = new BashHarness();
        h.Submit("NAME=world\ncat <<'EOF'\nhello, $NAME\nEOF\n");
        Assert.Contains("hello, $NAME", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void HeredocDashStripsLeadingTabs()
    {
        var h = new BashHarness();
        h.Submit("cat <<-EOF\n\tbody\n\tEOF\n");
        Assert.Contains("body", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void HereStringFeedsSingleLine()
    {
        var h = new BashHarness();
        h.Submit("cat <<< \"hi there\"\n");
        Assert.Contains("hi there", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BraceExpansionLiterals()
    {
        var h = new BashHarness();
        h.Submit("echo pre-{a,b,c}-post\n");
        Assert.Contains("pre-a-post pre-b-post pre-c-post", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BraceExpansionNumericRange()
    {
        var h = new BashHarness();
        h.Submit("echo {1..5}\n");
        Assert.Contains("1 2 3 4 5", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BraceExpansionLetterRange()
    {
        var h = new BashHarness();
        h.Submit("echo {a..c}\n");
        Assert.Contains("a b c", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void BraceExpansionCrossProduct()
    {
        var h = new BashHarness();
        h.Submit("echo {a,b}{1,2}\n");
        Assert.Contains("a1 a2 b1 b2", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobExpandsInCurrentDir()
    {
        var h = new BashHarness();
        h.Vfs.CreateTextFile("/work/a.txt", "", overwrite: false);
        h.Vfs.CreateTextFile("/work/b.txt", "", overwrite: false);
        h.Vfs.CreateTextFile("/work/c.doc", "", overwrite: false);
        h.Submit("cd /work\necho *.txt\n");
        var text = h.Output;
        Assert.Contains("a.txt", text, StringComparison.Ordinal);
        Assert.Contains("b.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c.doc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobExpandsAgainstAbsolutePath()
    {
        var h = new BashHarness();
        h.Vfs.CreateTextFile("/work/x.sh", "", overwrite: false);
        h.Vfs.CreateTextFile("/work/y.sh", "", overwrite: false);
        h.Submit("for f in /work/*.sh; do echo $f; done\n");
        Assert.Contains("/work/x.sh", h.Output, StringComparison.Ordinal);
        Assert.Contains("/work/y.sh", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobQuestionMatchesSingleChar()
    {
        var h = new BashHarness();
        h.Vfs.CreateTextFile("/work/a.txt", "", overwrite: false);
        h.Vfs.CreateTextFile("/work/ab.txt", "", overwrite: false);
        h.Submit("cd /work\necho ?.txt\n");
        Assert.Contains("a.txt", h.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ab.txt", h.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobReturnsLiteralWhenNoMatch()
    {
        var h = new BashHarness();
        h.Submit("cd /tmp\necho *.none\n");
        Assert.Contains("*.none", h.Output, StringComparison.Ordinal);
    }
}
