using RobotLoadGen;

// One sub-command per step of a benchmark run; run-bench.ps1 calls them in order.
var command = args.Length > 0 ? args[0] : "help";
var options = Options.Parse(args.Skip(1).ToArray());

switch (command)
{
    case "smoke":
        SmokeScenario.Run(options.Get("api", "http://localhost:5000"));
        break;
    case "init":
        await new BenchDatabase(options.Require("db")).InitAsync(options.Require("repo"));
        break;
    case "seed":
        await new BenchDatabase(options.Require("db")).SeedAsync(options.Int("robots"), options.Int("backlog"), options.Require("hash-key"),
            options.Get("allow-any-db", "no") == "yes");
        break;
    case "bench":
        await BenchCommand.RunAsync(options);
        break;
    case "run":
        RobotScenario.Run(options.Require("api"), options.Int("robots"), TimeSpan.FromSeconds(options.Int("duration")),
            TimeSpan.FromSeconds(options.Int("warmup")), options.Require("label"), options.Require("out"));
        break;
    case "check":
        await new BenchDatabase(options.Require("db")).CheckAsync(options.Require("label"), options.Int("robots"), options.Require("out"));
        break;
    case "summary":
        Summary.Print(options.Require("results"));
        break;
    default:
        Console.WriteLine("usage: RobotLoadGen <bench|init|seed|run|check|summary|smoke> [--name value ...]");
        return 1;
}

return 0;
