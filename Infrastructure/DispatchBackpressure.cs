using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace RobotControllerApi.Infrastructure;

// Caps how many dispatch requests run at once, so a robot surge queues briefly or is told to
// back off, instead of every request piling onto PostgreSQL and the connection pool together.
//
//   request -> free permit?  yes -> run
//                            no  -> queue space? yes -> wait for a permit
//                                                no  -> 429 + Retry-After
//
// Only endpoints marked [EnableRateLimiting(PolicyName)] are limited; everything else is untouched.
public static class DispatchBackpressure
{
    public const string PolicyName = "dispatch";

    // Temporary development defaults. The real values are chosen from benchmark results.
    private const int DefaultPermitLimit = 64;
    private const int DefaultQueueLimit = 32;
    private const int DefaultRetryAfterSeconds = 1;

    public static IServiceCollection AddDispatchBackpressure(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = ReadPositive(configuration, "Backpressure:PermitLimit", DefaultPermitLimit);
        var queueLimit = ReadNonNegative(configuration, "Backpressure:QueueLimit", DefaultQueueLimit);
        var retryAfterSeconds = ReadPositive(configuration, "Backpressure:RetryAfterSeconds", DefaultRetryAfterSeconds);

        services.AddRateLimiter(options =>
        {
            // One shared pool for all dispatch endpoints, because they all compete for the same database.
            options.AddConcurrencyLimiter(PolicyName, limiter =>
            {
                limiter.PermitLimit = permitLimit; //requests allowed to run at the same time
                limiter.QueueLimit = queueLimit; //requests allowed to wait for a permit
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; //waiting robots are served in arrival order
            });

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(); //tells the robot when to poll again
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;

    private static int ReadNonNegative(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value >= 0 ? value : fallback;
}
