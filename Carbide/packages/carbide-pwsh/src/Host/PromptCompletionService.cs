using CarbideShellCore.Vfs;

namespace CarbidePwsh.Host;

internal sealed record PromptCompletionResult(
    string Before,
    string After,
    IReadOnlyList<string> Matches);

internal sealed class PromptCompletionService
{
    private static readonly char[] SegmentSeparators = ['|', ';', '\r', '\n'];

    private readonly ShellHost _host;

    public PromptCompletionService(ShellHost host)
    {
        _host = host;
    }

    public PromptCompletionResult? Complete(string buffer, int cursor)
    {
        var token = PromptToken.At(buffer, cursor);
        var previousTokens = GetPreviousTokensInSegment(buffer, token.Start);
        var commandName = GetCommandName(previousTokens);
        var isCommandPosition = commandName is null;

        if (TryCompleteVariable(token, out var variableResult))
            return variableResult;

        if (!isCommandPosition && TryCompleteParameter(token, commandName!, out var parameterResult))
            return parameterResult;

        if (TryCompletePath(token, preferPathCompletion: !isCommandPosition, out var pathResult))
            return pathResult;

        if (isCommandPosition)
            return CompleteCommand(token);

        return null;
    }

    private PromptCompletionResult? CompleteCommand(PromptToken token)
    {
        var query = Unquote(token.TextBeforeCursor).Text;
        var matches = _host.GetInteractiveCommandNames()
            .Where(name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length == 0
            ? null
            : new PromptCompletionResult(token.Before, token.After, matches);
    }

    private bool TryCompleteParameter(PromptToken token, string commandName, out PromptCompletionResult? result)
    {
        result = null;

        var query = Unquote(token.TextBeforeCursor).Text;
        if (!query.StartsWith("-", StringComparison.Ordinal))
            return false;

        var parameterPrefix = query[1..];
        var matches = PromptParameterCatalog.GetParameterNames(_host, commandName)
            .Select(name => "-" + name)
            .Where(name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length == 0 && parameterPrefix.Length == 0)
            return false;

        result = matches.Length == 0
            ? null
            : new PromptCompletionResult(token.Before, token.After, matches);
        return result is not null;
    }

    private bool TryCompleteVariable(PromptToken token, out PromptCompletionResult? result)
    {
        result = null;

        var (query, quote) = Unquote(token.TextBeforeCursor);
        if (quote is not null || !query.StartsWith("$", StringComparison.Ordinal))
            return false;

        var variableQuery = query[1..];
        var envPrefix = "env:";
        IReadOnlyList<string> names;

        if (variableQuery.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var envQuery = variableQuery[envPrefix.Length..];
            names = _host.Env.All.Keys
                .Where(name => name.StartsWith(envQuery, StringComparison.OrdinalIgnoreCase))
                .Select(name => "$env:" + name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            names = EnumerateVariableNames()
                .Where(name => name.StartsWith(variableQuery, StringComparison.OrdinalIgnoreCase))
                .Select(name => "$" + name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (names.Count == 0)
            return false;

        result = new PromptCompletionResult(token.Before, token.After, names);
        return true;

        IEnumerable<string> EnumerateVariableNames()
        {
            foreach (var variable in _host.Interpreter.Scope.SnapshotCurrent())
                yield return variable.Key;

            yield return "?";
            yield return "_";
            yield return "args";
            yield return "Error";
            yield return "ErrorActionPreference";
            yield return "HOME";
            yield return "LASTEXITCODE";
            yield return "Matches";
            yield return "PSCommandPath";
            yield return "PSItem";
            yield return "PSScriptRoot";
            yield return "PSVersionTable";
            yield return "PWD";
            yield return "this";
        }
    }

    private bool TryCompletePath(PromptToken token, bool preferPathCompletion, out PromptCompletionResult? result)
    {
        result = null;

        var (query, quote) = Unquote(token.TextBeforeCursor);
        if (!preferPathCompletion && !LooksPathLike(query))
            return false;

        var (rawParent, leafPrefix) = SplitRawParent(query);
        var listingPath = rawParent.Length == 0 ? "." : rawParent;
        IReadOnlyList<VfsNode> children;

        try
        {
            var parent = _host.Vfs.Normalize(listingPath);
            if (_host.Vfs.Resolve(parent) is not VfsDirectory)
                return false;

            children = _host.Vfs.List(parent, recursive: false, filter: null).ToArray();
        }
        catch
        {
            return false;
        }

        var matches = children
            .Where(child => child.Name.StartsWith(leafPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(child => FormatPathCompletion(rawParent, child, quote))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length == 0)
            return false;

        result = new PromptCompletionResult(token.Before, token.After, matches);
        return true;
    }

    private static IReadOnlyList<string> GetPreviousTokensInSegment(string buffer, int tokenStart)
    {
        var segmentStart = 0;
        for (var i = tokenStart - 1; i >= 0; i--)
        {
            if (SegmentSeparators.Contains(buffer[i]))
            {
                segmentStart = i + 1;
                break;
            }
        }

        return PromptToken.Tokenize(buffer[segmentStart..tokenStart])
            .Select(token => Unquote(token.Text).Text)
            .Where(token => token.Length > 0)
            .ToArray();
    }

    private static string? GetCommandName(IEnumerable<string> previousTokens)
    {
        foreach (var token in previousTokens)
        {
            if (token is "&" or ".")
                continue;

            if (IsRedirectionOperator(token))
                continue;

            return token;
        }

        return null;
    }

    private static bool IsRedirectionOperator(string token)
        => token is ">" or ">>" or "2>" or "2>>" or "*>"
            || token.StartsWith(">", StringComparison.Ordinal);

    private static bool LooksPathLike(string query)
    {
        if (query.Length == 0)
            return false;

        return query.StartsWith("/", StringComparison.Ordinal)
            || query.StartsWith("\\", StringComparison.Ordinal)
            || query.StartsWith(".", StringComparison.Ordinal)
            || query.StartsWith("~", StringComparison.Ordinal)
            || query.Contains('/', StringComparison.Ordinal)
            || query.Contains('\\', StringComparison.Ordinal)
            || (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':');
    }

    private static (string Parent, string LeafPrefix) SplitRawParent(string query)
    {
        if (query.Length == 0)
            return ("", "");

        var lastSlash = Math.Max(query.LastIndexOf('/'), query.LastIndexOf('\\'));
        if (lastSlash < 0)
            return ("", query);

        return (query[..(lastSlash + 1)], query[(lastSlash + 1)..]);
    }

    private static string FormatPathCompletion(string rawParent, VfsNode child, char? quote)
    {
        var separator = rawParent.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
        var completed = rawParent.Length == 0
            ? child.Name
            : rawParent.EndsWith("/", StringComparison.Ordinal) || rawParent.EndsWith("\\", StringComparison.Ordinal)
                ? rawParent + child.Name
                : rawParent + separator + child.Name;

        if (child.IsDirectory && !completed.EndsWith("/", StringComparison.Ordinal) && !completed.EndsWith("\\", StringComparison.Ordinal))
            completed += separator;

        return QuotePathIfNeeded(completed, quote);
    }

    private static string QuotePathIfNeeded(string path, char? preferredQuote)
    {
        var quote = preferredQuote;
        if (quote is null && !NeedsQuoting(path))
            return path;

        quote ??= '\'';
        return quote == '\''
            ? "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "\"" + path.Replace("`", "``", StringComparison.Ordinal).Replace("\"", "`\"", StringComparison.Ordinal) + "\"";
    }

    private static bool NeedsQuoting(string path)
        => path.Any(ch => char.IsWhiteSpace(ch) || ch is '(' or ')' or '[' or ']' or '{' or '}' or ';' or '|' or '&');

    private static (string Text, char? Quote) Unquote(string text)
    {
        if (text.Length == 0)
            return (text, null);

        var quote = text[0];
        if (quote is not ('\'' or '"'))
            return (text, null);

        return (text[1..], quote);
    }

    private sealed record PromptToken(int Start, int Cursor, int End, string Buffer)
    {
        public string Before => Buffer[..Start];

        public string After => Buffer[End..];

        public string Text => Buffer[Start..End];

        public string TextBeforeCursor => Buffer[Start..Cursor];

        public static PromptToken At(string buffer, int cursor)
        {
            foreach (var token in Tokenize(buffer))
            {
                if (cursor >= token.Start && cursor <= token.End)
                    return new PromptToken(token.Start, cursor, token.End, buffer);
            }

            return new PromptToken(cursor, cursor, cursor, buffer);
        }

        public static IReadOnlyList<TokenSpan> Tokenize(string buffer)
        {
            var tokens = new List<TokenSpan>();
            var i = 0;

            while (i < buffer.Length)
            {
                while (i < buffer.Length && IsTokenBoundary(buffer[i]))
                    i++;

                if (i >= buffer.Length)
                    break;

                var start = i;
                if (buffer[i] is '\'' or '"')
                {
                    var quote = buffer[i++];
                    while (i < buffer.Length && buffer[i] != quote)
                        i++;

                    if (i < buffer.Length)
                        i++;
                }
                else
                {
                    while (i < buffer.Length && !IsTokenBoundary(buffer[i]))
                        i++;
                }

                tokens.Add(new TokenSpan(start, i, buffer[start..i]));
            }

            return tokens;
        }

        private static bool IsTokenBoundary(char ch)
            => char.IsWhiteSpace(ch) || ch is '|' or ';' or ',' or '(' or ')' or '{' or '}' or '[' or ']' or '&';
    }

    private sealed record TokenSpan(int Start, int End, string Text);
}

internal static class PromptParameterCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ParametersByCommand =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Clear-Content"] = ["Path", "Force"],
            ["Clear-Item"] = ["Path", "Force"],
            ["Clear-Variable"] = ["Name"],
            ["Compare-Object"] = ["ReferenceObject", "DifferenceObject", "Property", "IncludeEqual", "ExcludeDifferent"],
            ["ConvertFrom-Json"] = ["InputObject", "AsHashtable"],
            ["ConvertTo-Json"] = ["InputObject", "Compress", "Depth"],
            ["Copy-Item"] = ["Path", "Destination", "Recurse", "Force"],
            ["ForEach-Object"] = ["Process"],
            ["Format-List"] = ["Property"],
            ["Format-Table"] = ["Property", "AutoSize"],
            ["Get-Alias"] = ["Name", "Definition"],
            ["Get-CarbideApp"] = ["Name"],
            ["Get-ChildItem"] = ["Path", "Recurse", "File", "Directory", "Filter"],
            ["Get-Command"] = ["Name", "CommandType"],
            ["Get-Content"] = ["Path", "Raw", "Encoding"],
            ["Get-Date"] = ["Format"],
            ["Get-Help"] = ["Name"],
            ["Get-Item"] = ["Path"],
            ["Get-Location"] = [],
            ["Get-Member"] = ["Name", "MemberType"],
            ["Get-Module"] = ["Name", "ListAvailable"],
            ["Get-PSDrive"] = ["Name"],
            ["Get-PSProvider"] = ["Name"],
            ["Get-Variable"] = ["Name", "ValueOnly"],
            ["Group-Object"] = ["Property"],
            ["Import-Module"] = ["Name", "PassThru"],
            ["Invoke-Bash"] = ["Command"],
            ["Invoke-Cmd"] = ["Command"],
            ["Invoke-Expression"] = ["Command"],
            ["Join-Path"] = ["Path", "ChildPath"],
            ["Measure-Object"] = ["Property", "Sum", "Average", "Minimum", "Maximum"],
            ["Move-Item"] = ["Path", "Destination", "Force"],
            ["New-Alias"] = ["Name", "Value", "Option", "Force", "PassThru"],
            ["New-Item"] = ["Path", "ItemType", "Value", "Force"],
            ["New-Variable"] = ["Name", "Value", "Force", "PassThru"],
            ["Out-String"] = ["InputObject", "Width"],
            ["Register-CarbideApp"] = ["Name", "Path"],
            ["Remove-Item"] = ["Path", "Recurse", "Force"],
            ["Remove-Variable"] = ["Name"],
            ["Rename-Item"] = ["Path", "NewName", "Force"],
            ["Resolve-Path"] = ["Path"],
            ["Select-Object"] = ["Property", "First", "Last", "Skip", "Unique"],
            ["Set-Alias"] = ["Name", "Value", "Option", "Force", "PassThru"],
            ["Set-Content"] = ["Path", "Value", "Encoding", "Force"],
            ["Set-Location"] = ["Path"],
            ["Set-StrictMode"] = ["Version", "Off"],
            ["Set-Variable"] = ["Name", "Value", "Force", "PassThru"],
            ["Sort-Object"] = ["Property", "Descending", "Unique"],
            ["Split-Path"] = ["Path", "Parent", "Leaf", "Extension", "LeafBase", "Qualifier", "NoQualifier", "IsAbsolute"],
            ["Start-Sleep"] = ["Seconds", "Milliseconds"],
            ["Tee-Object"] = ["FilePath", "Variable", "Append"],
            ["Unregister-CarbideApp"] = ["Name"],
            ["Where-Object"] = ["FilterScript"],
            ["Write-Error"] = ["Message"],
            ["Write-Host"] = ["Object", "ForegroundColor", "BackgroundColor", "NoNewline"],
            ["Write-Output"] = ["InputObject"],
            ["Write-Warning"] = ["Message"]
        };

    public static IReadOnlyList<string> GetParameterNames(ShellHost host, string commandName)
    {
        if (host.Functions.TryGet(commandName, out var function))
            return function!.Parameters.Select(parameter => parameter.Name).ToArray();

        var canonicalName = host.Registry.ResolveAliasChain(commandName);
        if (!canonicalName.Equals(commandName, StringComparison.OrdinalIgnoreCase)
            && host.Functions.TryGet(canonicalName, out function))
        {
            return function!.Parameters.Select(parameter => parameter.Name).ToArray();
        }

        return ParametersByCommand.TryGetValue(canonicalName, out var parameters)
            ? parameters
            : Array.Empty<string>();
    }
}
