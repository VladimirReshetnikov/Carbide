using Shapes;

Shape[] shapes = [new Circle(1.0), new Rectangle(3.0, 4.0), new Triangle(6.0, 5.0)];
foreach (var s in shapes)
{
    Console.WriteLine($"{s.GetType().Name}: {Area.Of(s):F4}");
}
