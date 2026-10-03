using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace RobotLoadGen;

// N simulated robots, one per NBomber copy, each looping claim-next -> started -> completed on its own backlog.
public static class RobotScenario
{
    private const int MaxAttemptsOn429 = 20;

    public static void Run(string apiUrl, int robots, TimeSpan duration, TimeSpan warmUp, string label, string outPath, string reportFolder = "reports")
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) }; //re-resolves DNS each minute, so a load balancer's changing IPs get picked up
        using var http = new HttpClient(handler) { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(60) };
        var counters = new RunCounters();
        var drained = new bool[robots]; //robot i sets drained[i] when claim-next says its backlog is empty

        var scenario = Scenario.Create("robots", async context =>
        {
            var robot = context.ScenarioInfo.InstanceNumber % robots; //copy number -> robot number, so a copy always drives the same device
            var deviceId = BenchDatabase.FirstDeviceId + robot;

            if (drained[robot])
            {
                await Task.Delay(500); //backlog used up: stay quiet rather than flood the API with 204s
                return Response.Ok();
            }

            var jobId = 0;
            string? claimedAtUtc = null;
            TimeSpan? retryAfter = null;
            var noWork = false;

            var claim = await Step.Run("claim-next", context, async () =>
            {
                using var request = NewRequest(HttpMethod.Post, $"/api/adapter/devices/{deviceId}/work-items/claim-next", deviceId, new { });
                using var reply = await http.SendAsync(request);

                if (reply.StatusCode == HttpStatusCode.NoContent)
                {
                    noWork = true;
                    return Response.Ok(statusCode: "204");
                }

                if (reply.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    retryAfter = RetryAfter(reply);
                    return Response.Fail(statusCode: "429", message: "rejected by the limiter");
                }

                if (!reply.IsSuccessStatusCode) return Response.Fail(statusCode: Code(reply), message: await ShortBody(reply));

                using var body = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                var job = body.RootElement.GetProperty("job");
                jobId = job.GetProperty("id").GetInt32();
                claimedAtUtc = job.GetProperty("claimedAtUtc").GetString(); //kept as the exact string and echoed back, like the Nano does
                return Response.Ok(statusCode: "200");
            });

            if (noWork)
            {
                drained[robot] = true;
                return Response.Ok();
            }

            if (claim.IsError)
            {
                if (retryAfter.HasValue) await Task.Delay(retryAfter.Value); //the limiter said wait, so this robot waits before claiming again
                return Response.Fail();
            }

            if (!await SendUntilAcceptedAsync(http, context, "started", $"/api/adapter/jobs/{jobId}/started", deviceId, new { claimedAtUtc })) return Response.Fail();
            if (!await SendUntilAcceptedAsync(http, context, "completed", $"/api/adapter/jobs/{jobId}/completed", deviceId, new { claimedAtUtc, resultJson = "{}" })) return Response.Fail();

            Interlocked.Increment(ref counters.CompletedIncludingWarmUp);
            return Response.Ok();
        })
        .WithRestartIterationOnFail(false) //NBomber restarts the loop on any failed step by default, which skipped the Retry-After wait and abandoned claimed jobs
        .WithMaxFailCount(int.MaxValue) //failures under overload are results to record, not a reason to stop the run
        .WithWarmUpDuration(warmUp)
        .WithLoadSimulations(Simulation.KeepConstant(copies: robots, during: duration)); //closed loop: each robot waits for every reply before its next request

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("robot-dispatch")
            .WithTestName($"{label}-{robots}-robots")
            .WithReportFolder(Path.Combine(reportFolder, $"{label}-{robots}-robots"))
            .Run();

        BenchResults.Append(outPath, label, robots, duration, warmUp, stats, counters.CompletedIncludingWarmUp, drained.Count(x => x));
    }

    // started/completed must land, or the job sits Claimed until its lease runs out, so a 429 is retried after Retry-After.
    private static async Task<bool> SendUntilAcceptedAsync(HttpClient http, IScenarioContext context, string stepName, string path, int deviceId, object body)
    {
        for (var attempt = 1; attempt <= MaxAttemptsOn429; attempt++)
        {
            TimeSpan? retryAfter = null;
            var step = await Step.Run(stepName, context, async () =>
            {
                using var request = NewRequest(HttpMethod.Patch, path, deviceId, body);
                using var reply = await http.SendAsync(request);

                if (reply.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    retryAfter = RetryAfter(reply);
                    return Response.Fail(statusCode: "429", message: "rejected by the limiter");
                }

                return reply.IsSuccessStatusCode
                    ? Response.Ok(statusCode: Code(reply))
                    : Response.Fail(statusCode: Code(reply), message: await ShortBody(reply));
            });

            if (!step.IsError) return true;
            if (!retryAfter.HasValue) return false; //a real rejection (400, 409, 500): the job stays where it is and the DB check shows it
            await Task.Delay(retryAfter.Value);
        }

        return false;
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, int deviceId, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Device-Credential-Id", BenchDatabase.CredentialIdentifier(deviceId)); //the same two headers the Nano sends
        request.Headers.Add("X-Device-Credential-Secret", BenchDatabase.Secret(deviceId));
        return request;
    }

    private static TimeSpan RetryAfter(HttpResponseMessage reply) => reply.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);

    private static string Code(HttpResponseMessage reply) => ((int)reply.StatusCode).ToString(CultureInfo.InvariantCulture);

    private static async Task<string> ShortBody(HttpResponseMessage reply)
    {
        var text = await reply.Content.ReadAsStringAsync();
        return text.Length <= 200 ? text : text[..200];
    }

    private sealed class RunCounters
    {
        public int CompletedIncludingWarmUp;
    }
}

// One CSV row per step per run, straight from NBomber's statistics (warm-up already excluded by NBomber).
public static class BenchResults
{
    public const string Header = "label,robots,duration_s,warmup_s,step,ok,fail,ok_rps,p50_ms,p75_ms,p95_ms,p99_ms,mean_ms,max_ms,status_codes,completed_incl_warmup,robots_drained,run_at_utc";

    public static void Append(string outPath, string label, int robots, TimeSpan duration, TimeSpan warmUp, NodeStats stats, int completedIncludingWarmUp, int robotsDrained)
    {
        var runAt = DateTime.UtcNow.ToString("O");
        var newFile = !File.Exists(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        using var writer = new StreamWriter(outPath, append: true);
        if (newFile) writer.WriteLine(Header);

        foreach (var step in stats.ScenarioStats.SelectMany(x => x.StepStats))
        {
            var codes = string.Join(";", step.Ok.StatusCodes.Concat(step.Fail.StatusCodes).Select(x => $"{x.StatusCode}={x.Count}"));
            writer.WriteLine(string.Join(",", label, robots, F(duration.TotalSeconds), F(warmUp.TotalSeconds), step.StepName,
                step.Ok.Request.Count, step.Fail.Request.Count, F(step.Ok.Request.RPS),
                F(step.Ok.Latency.Percent50), F(step.Ok.Latency.Percent75), F(step.Ok.Latency.Percent95), F(step.Ok.Latency.Percent99),
                F(step.Ok.Latency.MeanMs), F(step.Ok.Latency.MaxMs), codes, completedIncludingWarmUp, robotsDrained, runAt));
        }
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
