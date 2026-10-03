using System.Globalization;

namespace RobotLoadGen;

// Prints the old vs new comparison from the two CSV files the runs leave behind.
public static class Summary
{
    public static void Print(string resultsFolder)
    {
        var bench = ReadCsv(Path.Combine(resultsFolder, "bench.csv"));
        var checks = ReadCsv(Path.Combine(resultsFolder, "db_check.csv"));

        var runs = bench.GroupBy(r => (Label: r["label"], Robots: int.Parse(r["robots"]), At: r["run_at_utc"])).ToList();
        var seen = new Dictionary<(string, int), int>();

        Console.WriteLine();
        Console.WriteLine($"{"robots",6} {"version",7} {"jobs/s",8} {"claim p50/p95/p99 ms",22} {"started p95",11} {"completed p95",13} {"429s",6} {"other fails",11} {"drained",7} {"db completed",12} {"db failed",9} {"in flight",9}");

        foreach (var run in runs.OrderBy(r => r.Key.Robots).ThenBy(r => r.Key.Label == "old" ? 0 : 1).ThenBy(r => r.Key.At))
        {
            var key = (run.Key.Label, run.Key.Robots);
            var nth = seen[key] = seen.TryGetValue(key, out var n) ? n + 1 : 0; //the k-th run of a pair matches its k-th DB check
            var check = checks.Where(c => c["label"] == run.Key.Label && int.Parse(c["robots"]) == run.Key.Robots).ElementAtOrDefault(nth);

            var steps = run.ToDictionary(r => r["step"]);
            string P(string step, string column) => steps.TryGetValue(step, out var row) ? row[column] : "-";

            var limiterRejections = run.Sum(r => CodeCount(r["status_codes"], "429"));
            var otherFails = run.Sum(r => long.Parse(r["fail"])) - limiterRejections;
            var claimLatency = $"{P("claim-next", "p50_ms")}/{P("claim-next", "p95_ms")}/{P("claim-next", "p99_ms")}";
            var inFlight = check == null ? "-" : (long.Parse(check["claimed"]) + long.Parse(check["executing"])).ToString(CultureInfo.InvariantCulture);

            Console.WriteLine($"{run.Key.Robots,6} {run.Key.Label,7} {P("completed", "ok_rps"),8} {claimLatency,22} {P("started", "p95_ms"),11} {P("completed", "p95_ms"),13} " +
                              $"{limiterRejections,6} {otherFails,11} {run.First()["robots_drained"],7} {check?["completed"] ?? "-",12} {check?["failed"] ?? "-",9} {inFlight,9}");
        }

        Console.WriteLine();
        Console.WriteLine("jobs/s = completed per second (warm-up excluded). db completed includes warm-up. in flight = Claimed + Executing when the run stopped.");
    }

    private static long CodeCount(string codes, string code) =>
        codes.Split(';', StringSplitOptions.RemoveEmptyEntries)
             .Select(x => x.Split('='))
             .Where(x => x.Length == 2 && x[0] == code)
             .Sum(x => long.Parse(x[1], CultureInfo.InvariantCulture));

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path)) return [];
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        var header = lines[0].Split(',');
        return lines.Skip(1)
            .Select(l => l.Split(','))
            .Select(cells => header.Zip(cells).ToDictionary(x => x.First, x => x.Second))
            .ToList();
    }
}
