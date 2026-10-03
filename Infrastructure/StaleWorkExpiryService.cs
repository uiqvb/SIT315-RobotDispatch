using RobotControllerApi.BoundedContexts.Jobs.Services;

namespace RobotControllerApi.Infrastructure;

// Drains expired leases on a timer.
//
// Without this, expiry only happened inside claim-next, which meant the queue could only be
// unblocked by the very robot that had stopped responding. A robot that browns out or drops
// off mid-run would leave work Executing forever, and the per-device queue cap would then
// reject every new job until someone intervened by hand.
//
// Every replica runs this service, but a PostgreSQL advisory lock lets only one of them sweep per tick.
public class StaleWorkExpiryService : BackgroundService
{
    // Same key on every replica, so they all contend for the same lock.
    public const long SweepLockKey = 315_331_001;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StaleWorkExpiryService> _logger;
    private readonly PostgresAdvisoryLock _sweepLock;
    private readonly TimeSpan _interval;

    public StaleWorkExpiryService(IServiceProvider serviceProvider, ILogger<StaleWorkExpiryService> logger, IConfiguration configuration, PostgresAdvisoryLock sweepLock)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _sweepLock = sweepLock;

        var configuredSeconds = int.TryParse(configuration["WorkDispatch:StaleExpirySweepSeconds"], out var parsed) && parsed > 0 ? parsed : 15;
        _interval = TimeSpan.FromSeconds(configuredSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunSweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep must never take the host down; the next tick tries again.
                _logger.LogError(ex, "Stale work sweep failed.");
            }
        }
    }

    // One tick. Returns null if another replica held the lock, else the number of jobs settled.
    public async Task<int?> RunSweepOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var held = await _sweepLock.TryAcquireAsync(SweepLockKey, cancellationToken); //non-blocking try, released when this scope exits
        if (held == null)
        {
            _logger.LogDebug("Stale work sweep skipped: another replica holds the sweep lock.");
            return null; //another replica is sweeping this tick, skip cleanly
        }

        // The dispatch service is scoped, so the sweep needs its own scope per tick.
        using var scope = _serviceProvider.CreateScope();
        var dispatchService = scope.ServiceProvider.GetRequiredService<IWorkDispatchService>();

        var expired = await dispatchService.ExpireStaleWorkForAllDevicesAsync(cancellationToken);
        if (expired > 0)
        {
            _logger.LogInformation("Stale work sweep settled {ExpiredCount} expired job lease(s).", expired);
        }

        return expired;
    }
}
