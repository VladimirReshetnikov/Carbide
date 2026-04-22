using Xunit;

namespace CarbideBash.Tests;

public class InterpreterTests
{
    [Fact]
    public void EchoPrints()
    {
        var h = new BashHarness();
        h.Submit("echo hello world\n");
        Assert.Equal("hello world" + Environment.NewLine, h.Output);
    }

    [Fact]
    public void AssignmentThenUse()
    {
        var h = new BashHarness();
        h.Submit("FOO=bar\necho $FOO\n");
        Assert.Contains("bar", h.Output);
    }

    [Fact]
    public void IfTrueTakesThen()
    {
        var h = new BashHarness();
        h.Submit("if true; then echo yes; else echo no; fi\n");
        Assert.Contains("yes", h.Output);
        Assert.DoesNotContain("no", h.Output);
    }

    [Fact]
    public void IfFalseTakesElse()
    {
        var h = new BashHarness();
        h.Submit("if false; then echo yes; else echo no; fi\n");
        Assert.DoesNotContain("yes", h.Output);
        Assert.Contains("no", h.Output);
    }

    [Fact]
    public void ForLoopIterates()
    {
        var h = new BashHarness();
        h.Submit("for x in a b c; do echo $x; done\n");
        var expected = $"a{Environment.NewLine}b{Environment.NewLine}c{Environment.NewLine}";
        Assert.Equal(expected, h.Output);
    }

    [Fact]
    public void WhileLoopWithCounter()
    {
        var h = new BashHarness();
        h.Submit("i=0\nwhile [ $i -lt 3 ]; do echo $i; i=$((i+1)); done\n");
        Assert.Contains("0", h.Output);
        Assert.Contains("1", h.Output);
        Assert.Contains("2", h.Output);
    }

    [Fact]
    public void CommandSubstitution()
    {
        var h = new BashHarness();
        h.Submit("echo result=$(echo inside)\n");
        Assert.Contains("result=inside", h.Output);
    }

    [Fact]
    public void RedirectsToFile()
    {
        var h = new BashHarness();
        h.Submit("echo hello > /tmp/out.txt\n");
        var node = h.Vfs.Resolve("/tmp/out.txt");
        Assert.NotNull(node);
    }

    [Fact]
    public void ReadsFromRedirectedInput()
    {
        var h = new BashHarness();
        h.Vfs.CreateTextFile("/tmp/in.txt", "one\ntwo\nthree", overwrite: false);
        h.Submit("cat /tmp/in.txt\n");
        Assert.Contains("one", h.Output);
        Assert.Contains("three", h.Output);
    }

    [Fact]
    public void ArithmeticInDoubleParen()
    {
        var h = new BashHarness();
        h.Submit("X=$((2+3*4))\necho $X\n");
        Assert.Contains("14", h.Output);
    }

    [Fact]
    public void ParameterDefault()
    {
        var h = new BashHarness();
        h.Submit("echo ${UNSET:-fallback}\n");
        Assert.Contains("fallback", h.Output);
    }

    [Fact]
    public void ParameterLength()
    {
        var h = new BashHarness();
        h.Submit("X=hello\necho ${#X}\n");
        Assert.Contains("5", h.Output);
    }

    [Fact]
    public void ParameterPrefixStrip()
    {
        var h = new BashHarness();
        h.Submit("X=hello.txt\necho ${X%.txt}\n");
        Assert.Contains("hello", h.Output);
    }

    [Fact]
    public void TestMinusFDetectsFile()
    {
        var h = new BashHarness();
        h.Vfs.CreateTextFile("/tmp/f.txt", "", overwrite: false);
        h.Submit("if [ -f /tmp/f.txt ]; then echo yes; fi\n");
        Assert.Contains("yes", h.Output);
    }

    [Fact]
    public void NumericTestEq()
    {
        var h = new BashHarness();
        h.Submit("if [ 3 -eq 3 ]; then echo match; fi\n");
        Assert.Contains("match", h.Output);
    }

    [Fact]
    public void StringTestEqual()
    {
        var h = new BashHarness();
        h.Submit("if [ abc = abc ]; then echo same; fi\n");
        Assert.Contains("same", h.Output);
    }

    [Fact]
    public void FunctionDefineAndCall()
    {
        var h = new BashHarness();
        h.Submit("greet() { echo hi $1; }\ngreet world\n");
        Assert.Contains("hi world", h.Output);
    }

    [Fact]
    public void PipelineFeedsInput()
    {
        var h = new BashHarness();
        h.Submit("echo line | cat\n");
        Assert.Contains("line", h.Output);
    }

    [Fact]
    public void PwdPrintsLocation()
    {
        var h = new BashHarness();
        h.Submit("cd /tmp\npwd\n");
        Assert.Contains("/tmp", h.Output);
    }

    [Fact]
    public void ExitCodePropagates()
    {
        var h = new BashHarness();
        var code = h.Submit("exit 7\n");
        Assert.Equal(7, code);
    }

    [Fact]
    public void CaseMatchesPattern()
    {
        var h = new BashHarness();
        h.Submit("case foo in f*) echo starts-with-f;; *) echo other;; esac\n");
        Assert.Contains("starts-with-f", h.Output);
    }
}
