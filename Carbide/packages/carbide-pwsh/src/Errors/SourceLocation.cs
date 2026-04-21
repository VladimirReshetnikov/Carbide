namespace CarbidePwsh.Errors;

public readonly record struct SourceLocation(int Line, int Column, int Offset, int Length)
{
    public static readonly SourceLocation None = new(0, 0, 0, 0);

    public override string ToString()
        => Line == 0 && Column == 0 ? "<unknown>" : $"({Line},{Column})";
}
