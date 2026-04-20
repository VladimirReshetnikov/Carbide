using System;
using System.Threading.Tasks;

Console.WriteLine("[DEBUG] boot");

Console.WriteLine("[DEBUG] awaiting CarbideConsole.DelayAsync(200)");
await Carbide.Terminal.CarbideConsole.DelayAsync(200);
Console.WriteLine("[DEBUG] delay returned ok");

Console.WriteLine("[DEBUG] done");
