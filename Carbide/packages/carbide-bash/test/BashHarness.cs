using CarbideBash.Host;
using CarbideShellCore.Vfs;

namespace CarbideBash.Tests;

internal sealed class BashHarness
{
    public ShellHost Host { get; }
    public StringWriter Stdout { get; } = new();
    public StringWriter Stderr { get; } = new();
    public StringReader Stdin { get; set; } = new("");

    public BashHarness() { Host = new ShellHost(); }

    public int Submit(string source) => Host.Submit(source, Stdin, Stdout, Stderr);
    public string Output => Stdout.ToString();
    public string Errors => Stderr.ToString();
    public VirtualFileSystem Vfs => Host.Vfs;
}
