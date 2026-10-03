using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RobotControllerApi.BoundedContexts.Jobs.Dtos;
using RobotControllerApi.BoundedContexts.Jobs.Services;
using RobotControllerApi.Infrastructure;
using RobotControllerApi.Infrastructure.DataAccess.ADO;

namespace RobotControllerApi.Tests.Concurrency;

// Phase 2 F6a/F6b: background-only stale cleanup, coordinated across replicas by a PostgreSQL advisory lock.
[Collection("postgres")]
public class StaleSweepTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;

    public StaleSweepTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync() => _db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private JobADO Jobs() => new(_db.DbConfig);

    private WorkDispatchService Dispatch() => new(
        new JobADO(_db.DbConfig), new WorkflowADO(_db.DbConfig), new JobHistoryADO(_db.DbConfig),
        new WorkflowHistoryADO(_db.DbConfig), new DeviceStatusADO(_db.DbConfig), new MapADO(_db.DbConfig), _db.Configuration);

    private JobService JobService() => new(
        new JobADO(_db.DbConfig), new WorkflowADO(_db.DbConfig), new JobHistoryADO(_db.DbConfig),
        new WorkflowHistoryADO(_db.DbConfig), new DeviceStatusADO(_db.DbConfig), new MapADO(_db.DbConfig), _db.Configuration);

    // One instance of this stands in for one API replica's hosted service.
    private StaleWorkExpiryService Sweeper(Func<IWorkDispatchService>? dispatch = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => dispatch?.Invoke() ?? Dispatch());
        return new StaleWorkExpiryService(services.BuildServiceProvider(), NullLogger<StaleWorkExpiryService>.Instance,
            _db.Configuration, new PostgresAdvisoryLock(_db.DbConfig));
    }

    private Task<string> StatusOf(int jobId) => _db.ScalarAsync<string>("SELECT status FROM public.job WHERE id = @id;", ("id", jobId));

    private async Task<JobResponse> Claim(int credentialId) =>
        (await Dispatch().ClaimNextWorkItemAsync(PostgresFixture.NanoDeviceId, new ClaimJobRequest(), credentialId, PostgresFixture.NanoDeviceId))?.Job
        ?? throw new InvalidOperationException("Expected claim-next to hand out a job.");

    private async Task<(int CredentialId, int JobId)> StaleClaimedJobAsync()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        await Claim(credentialId);
        await _db.ExpireLeaseAsync(jobId);
        return (credentialId, jobId);
    }

    [Fact]
    public async Task ClaimNext_NoLongerExpiresStaleWork()
    {
        var (credentialId, jobId) = await StaleClaimedJobAsync();

        var result = await Dispatch().ClaimNextWorkItemAsync(PostgresFixture.NanoDeviceId, new ClaimJobRequest(), credentialId, PostgresFixture.NanoDeviceId);

        result.Should().BeNull();
        (await StatusOf(jobId)).Should().Be("Claimed");
    }

    [Fact]
    public async Task Sweep_RequeuesAStaleClaimedJob()
    {
        var (_, jobId) = await StaleClaimedJobAsync();

        (await Sweeper().RunSweepOnceAsync()).Should().Be(1);
        (await StatusOf(jobId)).Should().Be("Queued");
    }

    [Fact]
    public async Task Sweep_ExpiresAStaleExecutingJob_AndDoesNotRequeueIt()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = await Claim(credentialId);
        await JobService().MarkJobStartedAsync(jobId, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);
        await _db.ExpireLeaseAsync(jobId);

        (await Sweeper().RunSweepOnceAsync()).Should().Be(1);

        (await StatusOf(jobId)).Should().Be("Expired");
        (await _db.ScalarAsync<long>("SELECT count(*) FROM public.job WHERE status = 'Queued';")).Should().Be(0);
    }

    [Fact]
    public async Task GetStaleJobs_ReturnsOnlyExpiredClaimedOrExecutingRows()
    {
        var (_, staleJobId) = await StaleClaimedJobAsync();
        for (var i = 0; i < 10; i++) await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);

        var stale = await Jobs().GetStaleJobsAsync(DateTime.UtcNow);

        stale.Select(x => x.Id).Should().Equal(staleJobId);
    }

    [Fact]
    public async Task AdvisoryLock_SecondSession_CannotAcquireWhileTheFirstHoldsIt()
    {
        var replicaA = new PostgresAdvisoryLock(_db.DbConfig);
        var replicaB = new PostgresAdvisoryLock(_db.DbConfig);

        await using (var held = await replicaA.TryAcquireAsync(StaleWorkExpiryService.SweepLockKey))
        {
            held.Should().NotBeNull();
            (await replicaB.TryAcquireAsync(StaleWorkExpiryService.SweepLockKey)).Should().BeNull();
        }

        await using var afterRelease = await replicaB.TryAcquireAsync(StaleWorkExpiryService.SweepLockKey);
        afterRelease.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_WhileAnotherReplicaHoldsTheLock_SkipsCleanlyAndLeavesTheJob()
    {
        var (_, jobId) = await StaleClaimedJobAsync();

        await using (await new PostgresAdvisoryLock(_db.DbConfig).TryAcquireAsync(StaleWorkExpiryService.SweepLockKey))
        {
            (await Sweeper().RunSweepOnceAsync()).Should().BeNull();
            (await StatusOf(jobId)).Should().Be("Claimed");
        }

        (await Sweeper().RunSweepOnceAsync()).Should().Be(1);
        (await StatusOf(jobId)).Should().Be("Queued");
    }

    [Fact]
    public async Task Sweep_ThatThrows_StillReleasesTheLock()
    {
        await StaleClaimedJobAsync();
        var broken = Sweeper(() => throw new InvalidOperationException("simulated sweep failure"));

        var act = () => broken.RunSweepOnceAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var next = await new PostgresAdvisoryLock(_db.DbConfig).TryAcquireAsync(StaleWorkExpiryService.SweepLockKey);
        next.Should().NotBeNull();
    }

    [Fact]
    public async Task ManyReplicasSweepingAtOnce_SettleEachStaleJobExactlyOnce()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobIds = new List<int>();
        for (var i = 0; i < 20; i++)
        {
            var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
            var claim = await Claim(credentialId);
            await JobService().MarkJobStartedAsync(claim.Id, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);
            jobIds.Add(jobId);
        }
        foreach (var jobId in jobIds) await _db.ExpireLeaseAsync(jobId);

        // Eight replicas fire on the same tick.
        var gate = new TaskCompletionSource();
        var sweeps = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => { await gate.Task; return await Sweeper().RunSweepOnceAsync(); })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(sweeps);

        results.Sum(x => x ?? 0).Should().Be(20);
        (await _db.ScalarAsync<long>("SELECT count(*) FROM public.job WHERE status = 'Expired';")).Should().Be(20);
        (await _db.ScalarAsync<long>("SELECT count(*) FROM public.jobhistory WHERE failurecode = 'LEASE_EXPIRED';")).Should().Be(20);
    }
}
