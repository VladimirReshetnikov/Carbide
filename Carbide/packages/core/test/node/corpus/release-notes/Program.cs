using Corp.Release;

var changes = new[]
{
    new Change(ChangeKind.Fix, "Compiler", "Respect deterministic source path mapping in pdb output."),
    new Change(ChangeKind.Feature, "Compiler", "Add carbde.lock.json replay mode for project runs."),
    new Change(ChangeKind.Security, "CLI", "Refuse analyzer-bearing packages in strict allow-list mode."),
    new Change(ChangeKind.Breaking, "CLI", "Drop implicit default for --assembly-name when multiple projects are passed."),
    new Change(ChangeKind.Fix, "CLI", "Improve JSON diagnostics span accuracy for generated files."),
};

Console.Write(Formatter.Render(changes));
