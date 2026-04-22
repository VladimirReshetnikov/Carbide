// CarbidePwsh.Parity — run a fixed set of expression / cmdlet scenarios against both the
// real pwsh.exe (7.6) and carbide-pwsh's ShellHost, capture stdout, and emit a
// side-by-side Markdown diff report so we can attack parity gaps systematically.
//
// Usage:
//   dotnet run --project parity/CarbidePwsh.Parity.csproj -- [--write report.md]
//
// Without --write, prints a condensed PASS/FAIL summary to stdout.

using System.Diagnostics;
using System.Text;
using CarbidePwsh.Host;

// Sub-commands. Default mode is the parity matrix; `parse <file>` is a focused
// parse-only check used when we aim carbide-pwsh at a real-world script.
if (args.Length >= 1 && args[0] == "parse")
{
    return CarbidePwsh.Parity.ParseFile.Run(args);
}

var pwshPath = Environment.GetEnvironmentVariable("PWSH")
    ?? @"C:\Program Files\PowerShell\7\pwsh.exe";

string? writeReportPath = null;
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--write" || args[i] == "-w") && i + 1 < args.Length)
    {
        writeReportPath = args[i + 1];
        i++;
    }
}

var scenarios = Scenarios.All;

Console.WriteLine($"Running {scenarios.Count} parity scenarios against:");
Console.WriteLine($"  real pwsh : {pwshPath}");
Console.WriteLine($"  carbide   : in-process CarbidePwsh.Host.ShellHost");
Console.WriteLine();

var report = new StringBuilder();
report.AppendLine("# carbide-pwsh vs pwsh.exe 7.6 — parity report");
report.AppendLine();
report.AppendLine($"- Reference: `{pwshPath}`");
report.AppendLine($"- Candidate: in-process `CarbidePwsh.Host.ShellHost`");
report.AppendLine($"- Scenarios: {scenarios.Count}");
report.AppendLine();

int matched = 0, diffed = 0;
foreach (var scenario in scenarios)
{
    var real = RunRealPwsh(pwshPath, scenario.Source);
    var carbide = RunCarbidePwsh(scenario.Source);

    bool match = NormalizeForCompare(real) == NormalizeForCompare(carbide);
    if (match) matched++; else diffed++;

    Console.Write(match ? '.' : 'X');

    report.AppendLine($"## {scenario.Name}  {(match ? "✅" : "❌")}");
    report.AppendLine();
    report.AppendLine("```powershell");
    report.AppendLine(scenario.Source);
    report.AppendLine("```");
    report.AppendLine();

    if (match)
    {
        report.AppendLine("<details><summary>Output (identical after ANSI-stripping)</summary>");
        report.AppendLine();
        report.AppendLine("```");
        report.AppendLine(Visible(real));
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("</details>");
    }
    else
    {
        report.AppendLine("### Real pwsh.exe");
        report.AppendLine("```");
        report.AppendLine(Visible(real));
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("### carbide-pwsh");
        report.AppendLine("```");
        report.AppendLine(Visible(carbide));
        report.AppendLine("```");
    }
    report.AppendLine();
}

Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"matched: {matched}   diffed: {diffed}   total: {scenarios.Count}");

if (writeReportPath is not null)
{
    await File.WriteAllTextAsync(writeReportPath, report.ToString());
    Console.WriteLine($"wrote report: {writeReportPath}");
}

return diffed == 0 ? 0 : 1;

static string RunRealPwsh(string exe, string source)
{
    var psi = new ProcessStartInfo
    {
        FileName = exe,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-NonInteractive");
    psi.ArgumentList.Add("-Command");
    psi.ArgumentList.Add(source);

    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit(30_000);
    return string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n[stderr]\n" + stderr;
}

static string RunCarbidePwsh(string source)
{
    var host = new ShellHost();
    var sw = new StringWriter();
    var savedOut = Console.Out;
    var savedErr = Console.Error;
    try
    {
        Console.SetOut(sw);
        Console.SetError(sw);
        try
        {
            host.SubmitAndRender(source, sw);
        }
        catch (Exception ex)
        {
            sw.WriteLine($"[exception] {ex.GetType().Name}: {ex.Message}");
        }
    }
    finally
    {
        Console.SetOut(savedOut);
        Console.SetError(savedErr);
    }
    return sw.ToString();
}

/// <summary>
/// Normalize output for comparison: strip ANSI escape sequences, trim trailing
/// whitespace on each line, collapse consecutive blank lines, and trim leading/trailing
/// blanks. Parity scoring that way focuses on content, not on cosmetic color/layout
/// differences — those get their own visual inspection.
/// </summary>
static string NormalizeForCompare(string s)
{
    s = StripAnsi(s);
    var lines = s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
    while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
    while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    var collapsed = new List<string>();
    foreach (var l in lines)
    {
        if (l.Length == 0 && collapsed.Count > 0 && collapsed[^1].Length == 0) continue;
        collapsed.Add(l);
    }
    return string.Join('\n', collapsed);
}

/// <summary>Strip CSI/OSC/SGR ANSI escape sequences so the comparison text is plain.</summary>
static string StripAnsi(string s)
{
    var sb = new StringBuilder(s.Length);
    int i = 0;
    while (i < s.Length)
    {
        var c = s[i];
        if (c == '\x1b' && i + 1 < s.Length)
        {
            var next = s[i + 1];
            if (next == '[')
            {
                i += 2;
                while (i < s.Length && !(s[i] >= 0x40 && s[i] <= 0x7e)) i++;
                if (i < s.Length) i++;
                continue;
            }
            if (next == ']')
            {
                i += 2;
                while (i < s.Length && s[i] != '\x07' && !(s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '\\')) i++;
                if (i < s.Length && s[i] == '\x07') i++;
                else if (i + 1 < s.Length) i += 2;
                continue;
            }
        }
        sb.Append(c);
        i++;
    }
    return sb.ToString();
}

/// <summary>Replace control bytes with printable sentinels so the report stays readable.</summary>
static string Visible(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
    {
        if (c == '\r') sb.Append("⟨CR⟩");
        else if (c == '\n') sb.Append('\n');
        else if (c == '\x1b') sb.Append("⟨ESC⟩");
        else if (c < 0x20 || c == 0x7f) sb.Append($"⟨{(int)c:X2}⟩");
        else sb.Append(c);
    }
    return sb.ToString();
}

internal static class Scenarios
{
    public static IReadOnlyList<(string Name, string Source)> All { get; } = new (string, string)[]
    {
        // Literal evaluation.
        ("int arithmetic",       "2 + 2"),
        ("string concat",        "'a' + 'b'"),
        ("range 1..5",           "1..5"),
        ("array literal",        "'hello', 'world'"),
        ("hashtable literal",    "@{ a = 1; b = 2 }"),
        ("int max value",        "[int]::MaxValue"),
        ("string length",        "'hello'.Length"),
        ("boolean",              "$true"),
        ("null",                 "$null"),
        ("double",               "3.14"),

        // Control flow.
        ("if-else true",         "if ($true) { 'yes' } else { 'no' }"),
        ("if-else false",        "if ($false) { 'yes' } else { 'no' }"),
        ("for 1..3 squared",     "foreach ($x in 1..3) { $x * $x }"),
        ("function def + call",  "function Add ($a, $b) { $a + $b }; Add 3 4"),

        // Cmdlets.
        ("Write-Output string",  "Write-Output 'hello'"),
        ("Write-Output numeric", "Write-Output 42"),
        ("Write-Output array",   "Write-Output 1,2,3"),
        ("Get-Date format",      "Get-Date -Format 'yyyy-MM-dd'"),

        // Pipeline shape.
        ("pipeline map",         "1..5 | ForEach-Object { $_ * $_ }"),
        ("pipeline filter",      "1..5 | Where-Object { $_ -gt 3 }"),
        ("pipeline select",      "1..3 | Select-Object -First 2"),
        ("pipeline sort",        "@(5,3,1,4,2) | Sort-Object"),

        // Strings.
        ("double-quote interp",  "$n = 3; \"n is $n\""),
        ("single-quote literal", "'$n is literal'"),
        ("string format -f",     "'{0:X}' -f 255"),
        ("string replace",       "'hello world' -replace 'world', 'universe'"),
        ("string match",         "'hello world' -match 'hello'"),

        // Operators.
        ("comparison -eq",       "3 -eq 3"),
        ("comparison -gt",       "5 -gt 3"),
        ("array -contains",      "@(1,2,3) -contains 2"),
        ("array -join",          "@('a','b','c') -join ','"),

        // Types.
        ("pstypename",           "(42).GetType().Name"),
        ("string to int",        "[int] '42'"),

        // Empty-output behaviors.
        ("empty array",          "@()"),
        ("empty pipeline",       "@() | ForEach-Object { $_ }"),

        // Error handling.
        ("try-catch throw",      "try { throw 'boom' } catch { \"caught: $($_.Exception.Message)\" }"),

        // Additional common idioms.
        ("nested array index",   "@(@(1,2), @(3,4))[1][0]"),
        ("range length",         "(1..10).Length"),
        ("string split",         "'a,b,c' -split ','"),
        ("sort desc",            "5,3,1,4,2 | Sort-Object -Descending"),
        ("where pipe",           "1..3 | Where-Object { $_ -ne 2 }"),
        ("ordered hashtable",    "[ordered]@{a=1; b=2; c=3}"),
        ("switch 1",             "switch (2) { 1 { 'one' } 2 { 'two' } default { 'other' } }"),
        ("math mod",             "10 % 3"),
        ("math div",             "10 / 3"),
        ("string interp quote",  "$x = 'world'; \"hello, $x!\""),
        ("string mult",          "'=' * 5"),
        ("negative index",       "(1..5)[-1]"),
        ("slice 1..3",           "(1..5)[1..3]"),
        ("explicit array",       "@(1)"),
        ("single-elem count",    "@(1).Count"),
        ("nested hash access",   "(@{a=@{b=42}}).a.b"),
        ("set and read var",     "$x = 42; $x"),
        ("multi-assign",         "$a, $b = 1, 2; \"$a,$b\""),

        // Providers.
        ("env read via $env",    "$env:PARITY_ONE = 'hi'; $env:PARITY_ONE"),
        ("env read via Get-Item","$env:PARITY_TWO = 'ok'; (Get-Item Env:PARITY_TWO).Value"),
        ("env Test-Path exists", "$env:PARITY_THREE = 'x'; Test-Path Env:PARITY_THREE"),
        ("env Test-Path absent", "Test-Path Env:NEVER_SET_ZZZZ999"),
        ("variable via $var:",   "$xv = 7; $variable:xv"),
        ("variable Get-Item",    "$xvi = 7; (Get-Item Variable:xvi).Value"),
        ("cd Variable then pwd", "Set-Location Variable:; (Get-Location).ToString()"),
        ("cd Function then pwd", "Set-Location Function:; (Get-Location).ToString()"),
        ("cd Alias then pwd",    "Set-Location Alias:; (Get-Location).ToString()"),
        ("cd Env then pwd",      "Set-Location Env:; (Get-Location).ToString()"),
    };
}
