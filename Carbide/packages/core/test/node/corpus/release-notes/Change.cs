namespace Corp.Release;

public enum ChangeKind
{
    Feature,
    Fix,
    Security,
    Breaking,
}

public sealed record Change(ChangeKind Kind, string Area, string Description);
