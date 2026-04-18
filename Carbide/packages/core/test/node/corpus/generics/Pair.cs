namespace MyLib;

public sealed class Pair<TFirst, TSecond>
{
    public Pair(TFirst first, TSecond second) { First = first; Second = second; }
    public TFirst First { get; }
    public TSecond Second { get; }
    public override string ToString() => $"({First}, {Second})";
}

public static class PairExtensions
{
    public static Pair<TSecond, TFirst> Swap<TFirst, TSecond>(this Pair<TFirst, TSecond> p)
        => new(p.Second, p.First);
}
