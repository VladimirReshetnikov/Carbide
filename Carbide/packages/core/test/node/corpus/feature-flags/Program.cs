using FeatureFlags;

const string Json = """
[
  { "name": "CheckoutV2", "enabled": true,  "ring": "prod" },
  { "name": "AiUpsell",   "enabled": false, "ring": "prod" },
  { "name": "TelemetryX", "enabled": true,  "ring": "all" },
  { "name": "PerfMode",   "enabled": true,  "ring": "dev" }
]
""";

var flags = FlagService.Parse(Json);
Console.WriteLine($"prod={FlagService.Evaluate(flags, "prod")}");
Console.WriteLine($"dev={FlagService.Evaluate(flags, "dev")}");
