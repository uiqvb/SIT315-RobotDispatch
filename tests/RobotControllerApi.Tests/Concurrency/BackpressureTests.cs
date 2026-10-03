using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RobotControllerApi.Infrastructure;

namespace RobotControllerApi.Tests.Concurrency;

// Phase 3 F8: the real dispatch limiter registration, on a real Kestrel server, with small limits so saturation is easy to reach.
public class BackpressureTests : IAsyncLifetime
{
    private const int PermitLimit = 2;
    private const int QueueLimit = 1;
    private const string RetryAfterSeconds = "7";

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _running;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backpressure:PermitLimit"] = PermitLimit.ToString(),
            ["Backpressure:QueueLimit"] = QueueLimit.ToString(),
            ["Backpressure:RetryAfterSeconds"] = RetryAfterSeconds
        });
        builder.Services.AddDispatchBackpressure(builder.Configuration);

        _app = builder.Build();
        _app.UseRateLimiter();

        // Holds its permit until the test releases it, so the test controls exactly how many are in flight.
        _app.MapGet("/dispatch", async () =>
        {
            Interlocked.Increment(ref _running);
            await _release.Task;
            return "ok";
        }).RequireRateLimiting(DispatchBackpressure.PolicyName);
        _app.MapGet("/other", () => "ok");

        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _release.TrySetResult();
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task WaitForRunning(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref _running) < expected)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Only {_running} of {expected} requests started.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task UnderTheLimit_RequestsRunNormally()
    {
        _release.SetResult();

        var response = await _client.GetAsync("/dispatch");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PermitsFull_NextRequestQueues_ThenRunsWhenAPermitFrees()
    {
        var first = _client.GetAsync("/dispatch");
        var second = _client.GetAsync("/dispatch");
        await WaitForRunning(PermitLimit);

        var queued = _client.GetAsync("/dispatch");
        await Task.Delay(200);
        queued.IsCompleted.Should().BeFalse(); //waiting in the queue, not rejected
        Volatile.Read(ref _running).Should().Be(PermitLimit); //and not running yet

        _release.SetResult();

        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        (await second).StatusCode.Should().Be(HttpStatusCode.OK);
        (await queued).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PermitsAndQueueFull_Returns429_WithRetryAfter()
    {
        var running = new[] { _client.GetAsync("/dispatch"), _client.GetAsync("/dispatch") };
        await WaitForRunning(PermitLimit);
        var queued = _client.GetAsync("/dispatch");
        await Task.Delay(200);

        var rejected = await _client.GetAsync("/dispatch");

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(int.Parse(RetryAfterSeconds)));

        _release.SetResult();
        await Task.WhenAll(running.Append(queued));
    }

    [Fact]
    public async Task WhileDispatchIsSaturated_UnlimitedEndpointsStillAnswer()
    {
        var running = new[] { _client.GetAsync("/dispatch"), _client.GetAsync("/dispatch") };
        await WaitForRunning(PermitLimit);
        var queued = _client.GetAsync("/dispatch");
        await Task.Delay(200);

        (await _client.GetAsync("/other")).StatusCode.Should().Be(HttpStatusCode.OK);

        _release.SetResult();
        await Task.WhenAll(running.Append(queued));
    }

    [Fact]
    public void OnlyTheFourDispatchEndpoints_CarryTheLimiter()
    {
        var limited = typeof(DispatchBackpressure).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName == DispatchBackpressure.PolicyName
                            || t.GetCustomAttribute<EnableRateLimitingAttribute>() != null)
                .Select(m => $"{t.Name}.{m.Name}"))
            .OrderBy(x => x)
            .ToList();

        limited.Should().Equal(
            "JobsController.MarkJobCompleted",
            "JobsController.MarkJobFailed",
            "JobsController.MarkJobStarted",
            "WorkDispatchController.ClaimNext");
    }
}
