using CarbideCmd.Host;
using CarbideShellCore.Vfs;

namespace CarbideCmd.Tests;

/// <summary>
/// xUnit helper that wraps a <see cref="ShellHost"/> with text-capturing output streams so
/// tests can assert against the rendered result without spinning a real terminal.
/// </summary>
internal sealed class CmdHarness
{
    public ShellHost Host { get; }
    public StringWriter Stdout { get; } = new();
    public StringWriter Stderr { get; } = new();
    public StringReader Stdin { get; set; } = new("");

    public CmdHarness()
    {
        Host = new ShellHost();
    }

    public int Submit(string source)
    {
        return Host.Submit(source, Stdin, Stdout, Stderr);
    }

    public string Output => Stdout.ToString();
    public string Errors => Stderr.ToString();
    public VirtualFileSystem Vfs => Host.Vfs;
}
