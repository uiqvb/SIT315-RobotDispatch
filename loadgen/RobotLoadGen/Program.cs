using System.Net;
using System.Net.Http.Json;
using NBomber.CSharp;

// Smoke test: proves NBomber, HTTP and the latency report work against the real API, with no DB access.
var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5000";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

var smoke = Scenario.Create("smoke_claim_next_no_credential", async context =>
{
    var claim = await Step.Run("claim-next", context, async () =>
    {
        using var reply = await http.PostAsJsonAsync("/api/adapter/devices/1/work-items/claim-next", new { }); //no credential headers, so auth rejects it before any DB lookup
        var status = ((int)reply.StatusCode).ToString();
        return reply.StatusCode == HttpStatusCode.Unauthorized
            ? Response.Ok(statusCode: status)
            : Response.Fail(statusCode: status, message: "expected 401"); //anything but 401 means the API did not behave as expected
    });

    return claim.IsError ? Response.Fail() : Response.Ok();
})
.WithoutWarmUp()
.WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: TimeSpan.FromSeconds(10))); //1 fake robot, back to back, for 10 s

NBomberRunner
    .RegisterScenarios(smoke)
    .WithReportFolder("reports")
    .Run();
