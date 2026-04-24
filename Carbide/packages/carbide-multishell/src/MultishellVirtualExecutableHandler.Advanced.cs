using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CarbideShellCore.Dispatch;
using CarbideShellCore.Errors;
using CarbideShellCore.Vfs;
using SharpCompress.Compressors.BZip2;

#if CARBIDE_PWSH_EMBEDDED_MULTISHELL
namespace CarbidePwsh.SharedMultishell;
#else
namespace CarbideMultishell;
#endif

internal sealed partial class MultishellVirtualExecutableHandler
{
    private static int ExecuteGnuCmp(VirtualExecutableInvocation invocation)
    {
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (files.Length != 2)
            return Unsupported(invocation, "expected two input files");

        var left = ReadRequiredFile(invocation, files[0]);
        var right = ReadRequiredFile(invocation, files[1]);
        var leftBytes = left.Content;
        var rightBytes = right.Content;
        int limit = Math.Min(leftBytes.Length, rightBytes.Length);
        int line = 1;
        for (int i = 0; i < limit; i++)
        {
            if (leftBytes[i] == (byte)'\n')
                line++;
            if (leftBytes[i] == rightBytes[i])
                continue;
            invocation.Output.WriteLine($"{files[0]} {files[1]} differ: byte {i + 1}, line {line}");
            return 1;
        }

        if (leftBytes.Length != rightBytes.Length)
        {
            invocation.Output.WriteLine($"{files[0]} {files[1]} differ: EOF on {(leftBytes.Length < rightBytes.Length ? files[0] : files[1])}");
            return 1;
        }
        return 0;
    }

    private static int ExecuteGnuComm(VirtualExecutableInvocation invocation)
    {
        bool showFirst = !invocation.Args.Contains("-1");
        bool showSecond = !invocation.Args.Contains("-2");
        bool showThird = !invocation.Args.Contains("-3");
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (files.Length != 2)
            return Unsupported(invocation, "expected two sorted input files");

        var left = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[0]).ReadText()).SkipLast(1).ToArray();
        var right = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[1]).ReadText()).SkipLast(1).ToArray();
        int i = 0, j = 0;
        while (i < left.Length || j < right.Length)
        {
            if (i >= left.Length)
            {
                if (showSecond) invocation.Output.WriteLine($"{(showFirst ? "\t" : "")}{right[j]}");
                j++;
                continue;
            }
            if (j >= right.Length)
            {
                if (showFirst) invocation.Output.WriteLine(left[i]);
                i++;
                continue;
            }

            int compare = string.CompareOrdinal(left[i], right[j]);
            if (compare == 0)
            {
                if (showThird)
                {
                    var prefix = (showFirst ? "\t" : "") + (showSecond ? "\t" : "");
                    invocation.Output.WriteLine(prefix + left[i]);
                }
                i++;
                j++;
            }
            else if (compare < 0)
            {
                if (showFirst) invocation.Output.WriteLine(left[i]);
                i++;
            }
            else
            {
                if (showSecond) invocation.Output.WriteLine($"{(showFirst ? "\t" : "")}{right[j]}");
                j++;
            }
        }
        return 0;
    }

    private static int ExecuteGnuDiff(VirtualExecutableInvocation invocation)
    {
        bool brief = invocation.Args.Contains("-q");
        bool context = invocation.Args.Contains("-c");
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (files.Length != 2)
            return Unsupported(invocation, "expected two input files");

        var leftPath = invocation.Vfs.Normalize(files[0]);
        var rightPath = invocation.Vfs.Normalize(files[1]);
        var left = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[0]).ReadText()).SkipLast(1).ToArray();
        var right = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[1]).ReadText()).SkipLast(1).ToArray();
        if (left.SequenceEqual(right, StringComparer.Ordinal))
            return 0;

        if (brief)
        {
            invocation.Output.WriteLine($"Files {files[0]} and {files[1]} differ");
            return 1;
        }

        if (context)
        {
            invocation.Output.WriteLine($"*** {leftPath}");
            invocation.Output.WriteLine($"--- {rightPath}");
        }
        else
        {
            invocation.Output.WriteLine($"--- {leftPath}");
            invocation.Output.WriteLine($"+++ {rightPath}");
        }

        int prefix = 0;
        while (prefix < left.Length && prefix < right.Length && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
            prefix++;
        int leftSuffix = left.Length - 1;
        int rightSuffix = right.Length - 1;
        while (leftSuffix >= prefix && rightSuffix >= prefix && string.Equals(left[leftSuffix], right[rightSuffix], StringComparison.Ordinal))
        {
            leftSuffix--;
            rightSuffix--;
        }

        if (!context)
            invocation.Output.WriteLine($"@@ -{prefix + 1},{Math.Max(0, leftSuffix - prefix + 1)} +{prefix + 1},{Math.Max(0, rightSuffix - prefix + 1)} @@");

        for (int i = 0; i < prefix; i++)
            if (!context)
                invocation.Output.WriteLine(" " + left[i]);
        for (int i = prefix; i <= leftSuffix; i++)
            invocation.Output.WriteLine((context ? "! " : "-") + left[i]);
        for (int i = prefix; i <= rightSuffix; i++)
            invocation.Output.WriteLine((context ? "! " : "+") + right[i]);
        for (int i = leftSuffix + 1; i < left.Length; i++)
            if (!context)
                invocation.Output.WriteLine(" " + left[i]);
        return 1;
    }

    private static int ExecuteGnuDiff3(VirtualExecutableInvocation invocation)
    {
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (files.Length != 3)
            return Unsupported(invocation, "expected three input files");

        var left = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[0]).ReadText()).SkipLast(1).ToArray();
        var @base = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[1]).ReadText()).SkipLast(1).ToArray();
        var right = SplitLinesPreserveTrailingEmpty(ReadRequiredFile(invocation, files[2]).ReadText()).SkipLast(1).ToArray();

        if (left.SequenceEqual(right, StringComparer.Ordinal))
        {
            foreach (var line in left)
                invocation.Output.WriteLine(line);
            return 0;
        }

        if (left.SequenceEqual(@base, StringComparer.Ordinal))
        {
            foreach (var line in right)
                invocation.Output.WriteLine(line);
            return 0;
        }

        if (right.SequenceEqual(@base, StringComparer.Ordinal))
        {
            foreach (var line in left)
                invocation.Output.WriteLine(line);
            return 0;
        }

        invocation.Output.WriteLine("<<<<<<< " + files[0]);
        foreach (var line in left)
            invocation.Output.WriteLine(line);
        invocation.Output.WriteLine("||||||| " + files[1]);
        foreach (var line in @base)
            invocation.Output.WriteLine(line);
        invocation.Output.WriteLine("=======");
        foreach (var line in right)
            invocation.Output.WriteLine(line);
        invocation.Output.WriteLine(">>>>>>> " + files[2]);
        return 1;
    }

    private static int ExecuteGnuPatch(VirtualExecutableInvocation invocation)
    {
        bool reverse = invocation.Args.Contains("-R");
        bool dryRun = invocation.Args.Contains("--dry-run");
        int strip = 0;
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "-p" && i + 1 < invocation.Args.Count)
                strip = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
        }

        var patchText = ReadAllText(invocation.Input);
        if (string.IsNullOrWhiteSpace(patchText))
            return Unsupported(invocation, "patch data must be supplied on stdin");

        var patches = ParseUnifiedPatch(patchText, strip);
        foreach (var filePatch in patches)
        {
            var targetPath = reverse ? filePatch.NewPath : filePatch.OldPath;
            var replacementPath = reverse ? filePatch.OldPath : filePatch.NewPath;
            var absolute = invocation.Vfs.Normalize(targetPath);
            var currentText = invocation.Vfs.Resolve(absolute) is VfsFile file ? file.ReadText() : "";
            var updated = ApplyUnifiedPatch(currentText, filePatch, reverse);
            if (!dryRun)
                invocation.Vfs.CreateTextFile(invocation.Vfs.Normalize(replacementPath), updated, overwrite: true);
        }
        return 0;
    }

    private sealed record UnifiedHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<string> Lines);
    private sealed record UnifiedFilePatch(string OldPath, string NewPath, IReadOnlyList<UnifiedHunk> Hunks);

    private static IReadOnlyList<UnifiedFilePatch> ParseUnifiedPatch(string patchText, int strip)
    {
        var lines = patchText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var results = new List<UnifiedFilePatch>();
        string? oldPath = null;
        string? newPath = null;
        List<UnifiedHunk>? hunks = null;
        int lineIndex = 0;
        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (oldPath is not null && newPath is not null && hunks is not null)
                    results.Add(new UnifiedFilePatch(oldPath, newPath, hunks.ToArray()));
                oldPath = StripPatchPath(line[4..], strip);
                newPath = null;
                hunks = new List<UnifiedHunk>();
                lineIndex++;
                continue;
            }
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = StripPatchPath(line[4..], strip);
                lineIndex++;
                continue;
            }
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = Regex.Match(line, @"@@ -(\d+),?(\d*) \+(\d+),?(\d*) @@");
                if (!match.Success || hunks is null)
                {
                    lineIndex++;
                    continue;
                }

                int oldStart = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int oldCount = string.IsNullOrEmpty(match.Groups[2].Value) ? 1 : int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                int newStart = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                int newCount = string.IsNullOrEmpty(match.Groups[4].Value) ? 1 : int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                var hunkLines = new List<string>();
                lineIndex++;
                while (lineIndex < lines.Length && !lines[lineIndex].StartsWith("@@ ", StringComparison.Ordinal) && !lines[lineIndex].StartsWith("--- ", StringComparison.Ordinal))
                {
                    hunkLines.Add(lines[lineIndex]);
                    lineIndex++;
                }
                hunks.Add(new UnifiedHunk(oldStart, oldCount, newStart, newCount, hunkLines));
                continue;
            }
            lineIndex++;
        }

        if (oldPath is not null && newPath is not null && hunks is not null)
            results.Add(new UnifiedFilePatch(oldPath, newPath, hunks.ToArray()));
        return results;
    }

    private static string StripPatchPath(string raw, int strip)
    {
        var path = raw.Split('\t', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join("/", parts.Skip(Math.Min(strip, parts.Length)));
    }

    private static string ApplyUnifiedPatch(string currentText, UnifiedFilePatch patch, bool reverse)
    {
        var inputLines = SplitLinesPreserveTrailingEmpty(currentText).ToList();
        if (inputLines.Count > 0 && inputLines[^1] == "")
            inputLines.RemoveAt(inputLines.Count - 1);

        var output = new List<string>();
        int index = 0;
        foreach (var hunk in patch.Hunks)
        {
            int targetStart = (reverse ? hunk.NewStart : hunk.OldStart) - 1;
            while (index < targetStart && index < inputLines.Count)
                output.Add(inputLines[index++]);

            foreach (var line in hunk.Lines)
            {
                if (line.Length == 0)
                    continue;
                char marker = line[0];
                string content = line[1..];
                switch (marker)
                {
                    case ' ':
                        output.Add(content);
                        index++;
                        break;
                    case '-':
                        if (reverse)
                            output.Add(content);
                        else
                            index++;
                        break;
                    case '+':
                        if (reverse)
                            index++;
                        else
                            output.Add(content);
                        break;
                }
            }
        }
        while (index < inputLines.Count)
            output.Add(inputLines[index++]);
        return string.Join('\n', output) + "\n";
    }

    private static int ExecuteGnuGzip(VirtualExecutableInvocation invocation)
        => ExecuteCompression(invocation, compress: true, extension: ".gz", compressor: CompressGZip, decompressor: DecompressGZip);

    private static int ExecuteGnuGunzip(VirtualExecutableInvocation invocation)
        => ExecuteCompression(invocation, compress: false, extension: ".gz", compressor: CompressGZip, decompressor: DecompressGZip);

    private static int ExecuteGnuBzip2(VirtualExecutableInvocation invocation)
        => ExecuteCompression(invocation, compress: true, extension: ".bz2", compressor: CompressBZip2, decompressor: DecompressBZip2);

    private static int ExecuteGnuBunzip2(VirtualExecutableInvocation invocation)
        => ExecuteCompression(invocation, compress: false, extension: ".bz2", compressor: CompressBZip2, decompressor: DecompressBZip2);

    private delegate byte[] ByteTransform(byte[] input);

    private static int ExecuteCompression(
        VirtualExecutableInvocation invocation,
        bool compress,
        string extension,
        ByteTransform compressor,
        ByteTransform decompressor)
    {
        bool toStdout = invocation.Args.Contains("-c");
        var files = invocation.Args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (files.Length == 0)
        {
            var input = Encoding.UTF8.GetBytes(ReadAllText(invocation.Input));
            var result = compress ? compressor(input) : decompressor(input);
            invocation.Output.Write(Encoding.UTF8.GetString(result));
            return 0;
        }

        foreach (var fileArg in files)
        {
            var file = ReadRequiredFile(invocation, fileArg);
            var result = compress ? compressor(file.Content) : decompressor(file.Content);
            if (toStdout)
            {
                invocation.Output.Write(Encoding.UTF8.GetString(result));
                continue;
            }

            var destination = compress
                ? file.AbsolutePath + extension
                : file.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    ? file.AbsolutePath[..^extension.Length]
                    : file.AbsolutePath + ".out";
            invocation.Vfs.CreateFile(destination, result, overwrite: true, encoding: file.Encoding);
        }
        return 0;
    }

    private static byte[] CompressGZip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(input, 0, input.Length);
        return output.ToArray();
    }

    private static byte[] DecompressGZip(byte[] input)
    {
        using var source = new MemoryStream(input);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CompressBZip2(byte[] input)
    {
        using var output = new MemoryStream();
        using (var bzip2 = new BZip2Stream(output, SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: false))
        {
            bzip2.Write(input, 0, input.Length);
            bzip2.Finish();
        }
        return output.ToArray();
    }

    private static byte[] DecompressBZip2(byte[] input)
    {
        using var source = new MemoryStream(input);
        using var bzip2 = new BZip2Stream(source, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true);
        using var output = new MemoryStream();
        bzip2.CopyTo(output);
        return output.ToArray();
    }

    private static int ExecuteGnuTar(VirtualExecutableInvocation invocation)
        => ExecuteTar(invocation);

    private static int ExecuteWindowsTar(VirtualExecutableInvocation invocation)
        => ExecuteTar(invocation);

    private static int ExecuteTar(VirtualExecutableInvocation invocation)
    {
        bool create = false, extract = false, list = false, verbose = false, gzip = false;
        string? archive = null;
        var inputs = new List<string>();

        foreach (var arg in invocation.Args)
        {
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1)
            {
                foreach (var flag in arg.Skip(1))
                {
                    switch (flag)
                    {
                        case 'c': create = true; break;
                        case 'x': extract = true; break;
                        case 't': list = true; break;
                        case 'v': verbose = true; break;
                        case 'z': gzip = true; break;
                    }
                }
                continue;
            }
            if (archive is null)
            {
                archive = arg;
                continue;
            }
            inputs.Add(arg);
        }

        if (archive is null)
            return Unsupported(invocation, "archive path is required");

        if (create)
        {
            using var output = new MemoryStream();
            Stream target = output;
            if (gzip)
                target = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
            using (var writer = new TarWriter(target, leaveOpen: true))
            {
                foreach (var input in inputs)
                    AddTarEntry(invocation, writer, invocation.Vfs.Normalize(input), VfsPath.SplitLeaf(invocation.Vfs.Normalize(input)).Leaf);
            }
            if (gzip && target is GZipStream gzipStream)
                gzipStream.Dispose();
            invocation.Vfs.CreateFile(invocation.Vfs.Normalize(archive), output.ToArray(), overwrite: true);
            return 0;
        }

        var archiveFile = ReadRequiredFile(invocation, archive);
        using var stream = new MemoryStream(archiveFile.Content);
        Stream source = stream;
        if (gzip)
            source = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(source, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (list)
            {
                invocation.Output.WriteLine(entry.Name);
                continue;
            }

            var destination = invocation.Vfs.Normalize(VfsPath.Join(invocation.Vfs.CurrentLocation, entry.Name));
            if (entry.EntryType is TarEntryType.Directory)
            {
                invocation.Vfs.GetOrCreateDirectory(destination);
                if (verbose) invocation.Output.WriteLine(entry.Name);
                continue;
            }

            using var entryStream = entry.DataStream;
            using var memory = new MemoryStream();
            entryStream?.CopyTo(memory);
            invocation.Vfs.CreateFile(destination, memory.ToArray(), overwrite: true);
            if (verbose) invocation.Output.WriteLine(entry.Name);
        }
        return 0;
    }

    private static void AddTarEntry(VirtualExecutableInvocation invocation, TarWriter writer, string absolutePath, string entryName)
    {
        if (invocation.Vfs.Resolve(absolutePath) is VfsDirectory directory)
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, entryName.EndsWith('/') ? entryName : entryName + "/"));
            foreach (var child in directory.Children.Values)
                AddTarEntry(invocation, writer, child.AbsolutePath, entryName.Length == 0 ? child.Name : entryName.TrimEnd('/') + "/" + child.Name);
            return;
        }

        if (invocation.Vfs.Resolve(absolutePath) is VfsFile file)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(file.Content, writable: false),
            };
            writer.WriteEntry(entry);
        }
    }

    private static int ExecuteGnuUnzip(VirtualExecutableInvocation invocation)
    {
        bool list = invocation.Args.Contains("-l");
        string destination = invocation.Vfs.CurrentLocation;
        string? archive = null;

        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "-d" && i + 1 < invocation.Args.Count)
            {
                destination = invocation.Args[++i];
                continue;
            }
            if (invocation.Args[i].StartsWith("-", StringComparison.Ordinal))
                continue;
            archive ??= invocation.Args[i];
        }

        if (archive is null)
            return Unsupported(invocation, "archive path is required");

        var file = ReadRequiredFile(invocation, archive);
        using var stream = new MemoryStream(file.Content);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            if (list)
            {
                invocation.Output.WriteLine(entry.FullName);
                continue;
            }

            var target = invocation.Vfs.Normalize(VfsPath.Join(destination, entry.FullName.Replace('\\', '/')));
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                invocation.Vfs.GetOrCreateDirectory(target);
                continue;
            }

            using var entryStream = entry.Open();
            using var memory = new MemoryStream();
            entryStream.CopyTo(memory);
            invocation.Vfs.CreateFile(target, memory.ToArray(), overwrite: true);
        }
        return 0;
    }

    private static int ExecuteGnuXargs(VirtualExecutableInvocation invocation)
    {
        bool nulSeparated = false;
        int? maxArgs = null;
        string? delimiter = null;
        string? replacement = null;
        var command = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            switch (invocation.Args[i])
            {
                case "-0":
                    nulSeparated = true;
                    break;
                case "-n" when i + 1 < invocation.Args.Count:
                    maxArgs = int.Parse(invocation.Args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-I" when i + 1 < invocation.Args.Count:
                    replacement = invocation.Args[++i];
                    break;
                case "-d" when i + 1 < invocation.Args.Count:
                    delimiter = invocation.Args[++i];
                    break;
                default:
                    command.Add(invocation.Args[i]);
                    break;
            }
        }

        if (command.Count == 0)
            command.Add("echo");

        var input = ReadAllText(invocation.Input);
        var tokens = nulSeparated
            ? input.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : delimiter is not null
                ? input.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : TokenizeWhitespace(input);

        if (replacement is not null)
        {
            foreach (var token in tokens)
            {
                var argv = command.Select(arg => arg.Replace(replacement, token, StringComparison.Ordinal)).ToArray();
                var code = DispatchCommand(invocation, argv[0], argv.Skip(1).ToArray(), "bash");
                if (code != 0)
                    return code;
            }
            return 0;
        }

        int batchSize = maxArgs ?? tokens.Length;
        for (int index = 0; index < tokens.Length; index += batchSize)
        {
            var batch = tokens.Skip(index).Take(batchSize).ToArray();
            var argv = command.Skip(1).Concat(batch).ToArray();
            var code = DispatchCommand(invocation, command[0], argv, "bash");
            if (code != 0)
                return code;
        }
        return 0;
    }

    private static int ExecuteGnuSed(VirtualExecutableInvocation invocation)
    {
        bool suppressPrint = invocation.Args.Contains("-n");
        var scriptBuilder = new StringBuilder();
        var files = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            if (invocation.Args[i] == "-e" && i + 1 < invocation.Args.Count)
            {
                scriptBuilder.AppendLine(invocation.Args[++i]);
                continue;
            }
            if (invocation.Args[i] == "-f" && i + 1 < invocation.Args.Count)
            {
                scriptBuilder.AppendLine(ReadRequiredFile(invocation, invocation.Args[++i]).ReadText());
                continue;
            }
            if (invocation.Args[i].StartsWith("-", StringComparison.Ordinal))
                continue;
            if (scriptBuilder.Length == 0)
                scriptBuilder.AppendLine(invocation.Args[i]);
            else
                files.Add(invocation.Args[i]);
        }

        var commands = ParseSedCommands(scriptBuilder.ToString());
        int code = 0;
        foreach (var (_, _, text) in EnumerateTexts(invocation, files))
        {
            var lines = SplitLinesPreserveTrailingEmpty(text).SkipLast(1).ToArray();
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var state = new SedLineState(lines[lineIndex]);
                foreach (var command in commands)
                {
                    if (!command.Matches(lineIndex + 1, lines.Length, state.Text))
                        continue;
                    command.Apply(state, invocation.Output);
                    if (state.Quit)
                        break;
                }
                if (!state.Deleted && !suppressPrint)
                    invocation.Output.WriteLine(state.Text);
                if (state.Quit)
                    return code;
            }
        }
        return code;
    }

    private sealed record SedAddress(Func<int, int, string, bool> Matches);
    private sealed class SedCommand(SedAddress? Start, SedAddress? End, Action<SedLineState, TextWriter> Action)
    {
        private bool _rangeActive;

        public bool Matches(int lineNumber, int totalLines, string text)
        {
            bool start = Start?.Matches(lineNumber, totalLines, text) ?? true;
            if (End is null)
                return start;

            if (!_rangeActive && start)
                _rangeActive = true;
            bool match = _rangeActive;
            if (_rangeActive && End.Matches(lineNumber, totalLines, text))
                _rangeActive = false;
            return match;
        }

        public void Apply(SedLineState state, TextWriter output) => Action(state, output);
    }

    private sealed class SedLineState(string text)
    {
        public string Text { get; set; } = text;
        public bool Deleted { get; set; }
        public bool Quit { get; set; }
    }

    private static IReadOnlyList<SedCommand> ParseSedCommands(string script)
    {
        var commands = new List<SedCommand>();
        foreach (var raw in script.Replace("\r\n", "\n", StringComparison.Ordinal).Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var working = raw.Trim();
            SedAddress? start = null;
            SedAddress? end = null;
            if (TryParseSedAddresses(ref working, out start, out end) && working.Length > 0)
            {
            }

            if (working.StartsWith("s/", StringComparison.Ordinal))
            {
                var parts = working.Split('/', 4);
                var pattern = parts.Length > 1 ? parts[1] : "";
                var replacement = parts.Length > 2 ? parts[2] : "";
                var flags = parts.Length > 3 ? parts[3] : "";
                var regex = new Regex(pattern, flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None);
                bool global = flags.Contains('g');
                commands.Add(new SedCommand(start, end, (state, _) =>
                {
                    state.Text = global ? regex.Replace(state.Text, replacement) : regex.Replace(state.Text, replacement, 1);
                }));
                continue;
            }

            switch (working[0])
            {
                case 'd':
                    commands.Add(new SedCommand(start, end, (state, _) => state.Deleted = true));
                    break;
                case 'p':
                    commands.Add(new SedCommand(start, end, (state, output) => output.WriteLine(state.Text)));
                    break;
                case 'q':
                    commands.Add(new SedCommand(start, end, (state, _) => state.Quit = true));
                    break;
                case 'a':
                    commands.Add(new SedCommand(start, end, (state, output) => output.WriteLine(working[1..].TrimStart())));
                    break;
                case 'i':
                    commands.Add(new SedCommand(start, end, (state, output) => output.WriteLine(working[1..].TrimStart())));
                    break;
                case 'c':
                    commands.Add(new SedCommand(start, end, (state, _) => state.Text = working[1..].TrimStart()));
                    break;
            }
        }
        return commands;
    }

    private static bool TryParseSedAddresses(ref string working, out SedAddress? start, out SedAddress? end)
    {
        start = null;
        end = null;
        int consumed = 0;
        if (!TryParseSedAddress(working, ref consumed, out start))
            return false;
        if (consumed < working.Length && working[consumed] == ',')
        {
            consumed++;
            TryParseSedAddress(working, ref consumed, out end);
        }
        working = working[consumed..].TrimStart();
        return true;
    }

    private static bool TryParseSedAddress(string working, ref int consumed, out SedAddress? address)
    {
        address = null;
        if (consumed >= working.Length)
            return false;

        if (char.IsDigit(working[consumed]))
        {
            int startIndex = consumed;
            while (consumed < working.Length && char.IsDigit(working[consumed]))
                consumed++;
            int lineNumber = int.Parse(working[startIndex..consumed], CultureInfo.InvariantCulture);
            address = new SedAddress((line, _, _) => line == lineNumber);
            return true;
        }

        if (working[consumed] == '$')
        {
            consumed++;
            address = new SedAddress((line, total, _) => line == total);
            return true;
        }

        if (working[consumed] == '/')
        {
            int endSlash = working.IndexOf('/', consumed + 1);
            if (endSlash < 0)
                return false;
            var regex = new Regex(working[(consumed + 1)..endSlash]);
            consumed = endSlash + 1;
            address = new SedAddress((_, _, text) => regex.IsMatch(text));
            return true;
        }
        return false;
    }

    private static int ExecuteGnuAwk(VirtualExecutableInvocation invocation)
    {
        string fs = " ";
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OFS"] = " ",
            ["ORS"] = "\n",
            ["FS"] = fs,
        };
        string? program = null;
        var files = new List<string>();
        for (int i = 0; i < invocation.Args.Count; i++)
        {
            switch (invocation.Args[i])
            {
                case "-F" when i + 1 < invocation.Args.Count:
                    fs = invocation.Args[++i];
                    variables["FS"] = fs;
                    break;
                case "-v" when i + 1 < invocation.Args.Count:
                {
                    var assignment = invocation.Args[++i];
                    var eq = assignment.IndexOf('=');
                    if (eq > 0)
                        variables[assignment[..eq]] = assignment[(eq + 1)..];
                    break;
                }
                case "-f" when i + 1 < invocation.Args.Count:
                    program = ReadRequiredFile(invocation, invocation.Args[++i]).ReadText();
                    break;
                default:
                    if (invocation.Args[i].StartsWith("-", StringComparison.Ordinal))
                        break;
                    if (program is null)
                        program = invocation.Args[i];
                    else
                        files.Add(invocation.Args[i]);
                    break;
            }
        }

        if (program is null)
            return Unsupported(invocation, "missing awk program");

        var rules = ParseAwkRules(program);
        ExecuteAwkBlocks(invocation, rules.Where(rule => rule.Kind == "BEGIN"), variables, null, 0, 0);
        int nr = 0;
        foreach (var (_, _, text) in EnumerateTexts(invocation, files))
        {
            int fnr = 0;
            foreach (var line in SplitLinesPreserveTrailingEmpty(text).SkipLast(1))
            {
                nr++;
                fnr++;
                var fields = string.IsNullOrEmpty(fs) || fs == " "
                    ? TokenizeWhitespace(line)
                    : line.Split(fs, StringSplitOptions.None);
                if (!ExecuteAwkBlocks(invocation, rules.Where(rule => rule.Kind == "BODY"), variables, (line, fields), nr, fnr))
                    return 0;
            }
        }
        ExecuteAwkBlocks(invocation, rules.Where(rule => rule.Kind == "END"), variables, null, nr, 0);
        return 0;
    }

    private sealed record AwkRule(string Kind, string? Pattern, string Body);

    private static IReadOnlyList<AwkRule> ParseAwkRules(string program)
    {
        var rules = new List<AwkRule>();
        var matches = Regex.Matches(program, @"(?s)(BEGIN|END|/[^/]+/)?\s*\{(.*?)\}");
        foreach (Match match in matches)
        {
            var pattern = match.Groups[1].Success ? match.Groups[1].Value : null;
            string kind = pattern switch
            {
                "BEGIN" => "BEGIN",
                "END" => "END",
                _ => "BODY",
            };
            rules.Add(new AwkRule(kind, pattern is "BEGIN" or "END" ? null : pattern, match.Groups[2].Value));
        }
        if (rules.Count == 0)
            rules.Add(new AwkRule("BODY", null, program));
        return rules;
    }

    private static bool ExecuteAwkBlocks(
        VirtualExecutableInvocation invocation,
        IEnumerable<AwkRule> rules,
        IDictionary<string, string> variables,
        (string Line, string[] Fields)? record,
        int nr,
        int fnr)
    {
        foreach (var rule in rules)
        {
            if (rule.Pattern is not null && record is not null)
            {
                var pattern = rule.Pattern.Trim('/');
                if (!Regex.IsMatch(record.Value.Line, pattern))
                    continue;
            }

            foreach (var statement in rule.Body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (statement.StartsWith("print", StringComparison.Ordinal))
                {
                    var value = EvaluateAwkExpression(statement[5..], variables, record, nr, fnr);
                    invocation.Output.Write(value);
                    invocation.Output.Write(variables.TryGetValue("ORS", out var ors) ? ors : "\n");
                    continue;
                }
                if (statement.StartsWith("printf", StringComparison.Ordinal))
                {
                    var pieces = SplitAwkArguments(statement[6..]);
                    if (pieces.Count == 0)
                        continue;
                    var format = EvaluateAwkExpression(pieces[0], variables, record, nr, fnr);
                    var values = pieces.Skip(1).Select(piece => (object)EvaluateAwkExpression(piece, variables, record, nr, fnr)).ToArray();
                    invocation.Output.Write(string.Format(CultureInfo.InvariantCulture, ConvertAwkFormat(format), values));
                    continue;
                }
                if (statement.Equals("next", StringComparison.Ordinal))
                    return true;
                if (statement.StartsWith("exit", StringComparison.Ordinal))
                    return false;
                var equals = statement.IndexOf('=');
                if (equals > 0)
                {
                    var name = statement[..equals].Trim();
                    variables[name] = EvaluateAwkExpression(statement[(equals + 1)..], variables, record, nr, fnr);
                }
            }
        }
        return true;
    }

    private static IReadOnlyList<string> SplitAwkArguments(string text)
    {
        var results = new List<string>();
        var current = new StringBuilder();
        bool inString = false;
        foreach (var ch in text)
        {
            if (ch == '"')
                inString = !inString;
            if (ch == ',' && !inString)
            {
                results.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
            results.Add(current.ToString());
        return results;
    }

    private static string ConvertAwkFormat(string format)
        => format.Replace("%s", "{0}", StringComparison.Ordinal)
            .Replace("%d", "{0:0}", StringComparison.Ordinal)
            .Replace("%f", "{0:F}", StringComparison.Ordinal);

    private static string EvaluateAwkExpression(string expression, IDictionary<string, string> variables, (string Line, string[] Fields)? record, int nr, int fnr)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
            return record?.Line ?? "";
        if (expression.StartsWith('"') && expression.EndsWith('"'))
            return Regex.Unescape(expression[1..^1]);
        if (expression == "$0")
            return record?.Line ?? "";
        if (expression.StartsWith('$') && int.TryParse(expression[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fieldNumber))
        {
            var fields = record?.Fields ?? [];
            return fieldNumber >= 1 && fieldNumber <= fields.Length ? fields[fieldNumber - 1] : "";
        }
        if (expression.Equals("NR", StringComparison.Ordinal))
            return nr.ToString(CultureInfo.InvariantCulture);
        if (expression.Equals("FNR", StringComparison.Ordinal))
            return fnr.ToString(CultureInfo.InvariantCulture);
        if (expression.Equals("NF", StringComparison.Ordinal))
            return (record?.Fields.Length ?? 0).ToString(CultureInfo.InvariantCulture);
        if (expression.StartsWith("tolower(", StringComparison.Ordinal) && expression.EndsWith(')'))
            return EvaluateAwkExpression(expression[8..^1], variables, record, nr, fnr).ToLowerInvariant();
        if (expression.StartsWith("toupper(", StringComparison.Ordinal) && expression.EndsWith(')'))
            return EvaluateAwkExpression(expression[8..^1], variables, record, nr, fnr).ToUpperInvariant();
        if (expression.StartsWith("length(", StringComparison.Ordinal) && expression.EndsWith(')'))
            return EvaluateAwkExpression(expression[7..^1], variables, record, nr, fnr).Length.ToString(CultureInfo.InvariantCulture);
        if (variables.TryGetValue(expression, out var value))
            return value;

        if (expression.Contains('+'))
        {
            var parts = expression.Split('+', 2);
            if (decimal.TryParse(EvaluateAwkExpression(parts[0], variables, record, nr, fnr), NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                && decimal.TryParse(EvaluateAwkExpression(parts[1], variables, record, nr, fnr), NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
            {
                return (left + right).ToString(CultureInfo.InvariantCulture);
            }
        }

        var concatenated = SplitAwkArguments(expression.Replace(" ", ",", StringComparison.Ordinal));
        if (concatenated.Count > 1)
            return string.Concat(concatenated.Select(part => EvaluateAwkExpression(part, variables, record, nr, fnr)));
        return expression;
    }

    private static int ExecuteApp(
        VirtualExecutableInvocation invocation,
        string appPath,
        IReadOnlyList<string> args,
        ShellExecutionContext ctx)
    {
        var file = invocation.Vfs.Resolve(appPath) as VfsFile;
        if (file is null)
        {
            invocation.Error.WriteLine($"Cannot find path '{appPath}' because it does not exist.");
            return 1;
        }

        Assembly asm;
        try
        {
            asm = Assembly.Load(file.Content);
        }
        catch (Exception ex)
        {
            invocation.Error.WriteLine($"Cannot load '{appPath}' as a .NET assembly: {ex.Message}");
            return 1;
        }

        var entry = asm.EntryPoint;
        if (entry is null)
        {
            invocation.Error.WriteLine($"No entry point in '{appPath}'.");
            return 1;
        }

        var stringArgs = args.ToArray();
        object? result;
        try
        {
            var parameters = entry.GetParameters();
            result = parameters.Length switch
            {
                0 => entry.Invoke(null, null),
                1 when parameters[0].ParameterType == typeof(string[]) => entry.Invoke(null, [stringArgs]),
                _ => throw new InvalidOperationException("Unsupported entry-point signature."),
            };
        }
        catch (TargetInvocationException tie)
        {
            invocation.Error.WriteLine(tie.InnerException?.Message ?? tie.Message);
            return 1;
        }

        return result switch
        {
            int code => code,
            Task<int> task => task.GetAwaiter().GetResult(),
            Task task => AwaitTask(task),
            _ => 0,
        };
    }

    private static int AwaitTask(Task task)
    {
        task.GetAwaiter().GetResult();
        return 0;
    }
}
