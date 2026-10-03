using System.Runtime.InteropServices;

namespace RobotLoadGen;

// The portable path: seed -> run -> check for each robot count against one target URL.
// The target can be a local API, one cloud server, or a load balancer in front of several replicas.
public static class BenchCommand
{
    public static async Task RunAsync(Options options)
    {
        var api = options.Require("api");
        var database = new BenchDatabase(options.Require("db"));
        var hashKey = options.Require("hash-key"); //must equal DeviceCredential:HashKey on every API replica, or every robot gets 401
        var label = options.Require("label");
        var robotCounts = options.Get("robots", "10,50,100").Split(',').Select(x => int.Parse(x.Trim())).ToArray();
        var backlog = int.Parse(options.Get("backlog", "2000"));
        var duration = TimeSpan.FromSeconds(int.Parse(options.Get("duration", "30")));
        var warmUp = TimeSpan.FromSeconds(int.Parse(options.Get("warmup", "5")));
        var repeats = int.Parse(options.Get("repeats", "1"));
        var allowAnyDatabase = options.Get("allow-any-db", "no") == "yes";
        var results = options.Get("results", Path.Combine("results", DateTime.Now.ToString("yyyyMMdd-HHmmss")));

        Directory.CreateDirectory(results);
        await File.WriteAllLinesAsync(Path.Combine(results, "run-info.txt"),
        [
            $"started:      {DateTime.Now:O}",
            $"target:       {api} (label {label})",
            $"note:         {options.Get("note", "-")}", //where the servers live, e.g. instance types, replica count, region
            $"load machine: {Environment.MachineName}, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs",
            $"parameters:   robots={string.Join(",", robotCounts)} duration={duration.TotalSeconds}s warmup={warmUp.TotalSeconds}s backlog={backlog} jobs/robot repeats={repeats}",
        ]);

        await WaitForApiAsync(api);

        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            foreach (var robots in robotCounts)
            {
                Console.WriteLine($"=== {label}, {robots} robots, repeat {repeat} ===");
                await database.SeedAsync(robots, backlog, hashKey, allowAnyDatabase);
                RobotScenario.Run(api, robots, duration, warmUp, label, Path.Combine(results, "bench.csv"), Path.Combine(results, "nbomber")); //NBomber's own reports stay with the results
                await database.CheckAsync(label, robots, Path.Combine(results, "db_check.csv"));
            }
        }

        Summary.Print(results);
    }

    // Any HTTP answer (a 401 here) means the API, or the load balancer in front of it, is up.
    private static async Task WaitForApiAsync(string api)
    {
        using var http = new HttpClient { BaseAddress = new Uri(api), Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var reply = await http.PostAsync($"/api/adapter/devices/{BenchDatabase.FirstDeviceId}/work-items/claim-next", new StringContent("{}"));
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                await Task.Delay(1000);
            }
        }
    }
}
