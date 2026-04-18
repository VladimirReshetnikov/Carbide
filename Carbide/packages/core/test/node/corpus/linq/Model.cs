namespace Data;

public sealed record Employee(string Name, string Department, decimal Salary);

public static class Seed
{
    public static readonly Employee[] All =
    [
        new Employee("Ada", "R&D", 120_000m),
        new Employee("Brooke", "Sales", 90_000m),
        new Employee("Cam", "R&D", 110_000m),
        new Employee("Drew", "Sales", 95_000m),
        new Employee("Eli", "R&D", 130_000m),
    ];
}
