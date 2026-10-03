using System.Text.Json;
using FluentAssertions;
using RobotControllerApi.BoundedContexts.Jobs.Dtos;
using RobotControllerApi.BoundedContexts.Jobs.Services;
using RobotControllerApi.Infrastructure.DataAccess.ADO;

namespace RobotControllerApi.Tests.Concurrency;

// Phase 1 F1/F2/F9: every assertion here runs against real PostgreSQL row locking, not a mock.
[Collection("postgres")]
public class DispatchConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;

    public DispatchConcurrencyTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync() => _db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private JobADO Jobs() => new(_db.DbConfig);

    private WorkDispatchService Dispatch() => new(
        new JobADO(_db.DbConfig), new WorkflowADO(_db.DbConfig), new JobHistoryADO(_db.DbConfig),
        new WorkflowHistoryADO(_db.DbConfig), new DeviceStatusADO(_db.DbConfig), new MapADO(_db.DbConfig), _db.Configuration);

    private JobService JobService() => new(
        new JobADO(_db.DbConfig), new WorkflowADO(_db.DbConfig), new JobHistoryADO(_db.DbConfig),
        new WorkflowHistoryADO(_db.DbConfig), new DeviceStatusADO(_db.DbConfig), new MapADO(_db.DbConfig), _db.Configuration);

    private Task<string> StatusOf(int jobId) => _db.ScalarAsync<string>("SELECT status FROM public.job WHERE id = @id;", ("id", jobId));

    private WorkItemClaimResponse ClaimNext(int credentialId, int deviceId = PostgresFixture.NanoDeviceId) =>
        Dispatch().ClaimNextWorkItem(deviceId, new ClaimJobRequest(), credentialId, deviceId)
        ?? throw new InvalidOperationException("Expected claim-next to hand out a job.");

    // Starts every task on the same signal so the attempts genuinely overlap.
    private static async Task<T[]> RunTogether<T>(int count, Func<T> attempt)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(_ => Task.Run(async () => { await gate.Task; return attempt(); })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task TryClaimJob_ConcurrentAttemptsOnOneJob_ExactlyOneWins()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var now = DateTime.UtcNow;

        var results = await RunTogether(32, () => Jobs().TryClaimJob(jobId, credentialId, now, now.AddMinutes(5)));

        results.Count(x => x != null).Should().Be(1);
        (await StatusOf(jobId)).Should().Be("Claimed");
    }

    [Fact]
    public async Task TryClaimJob_SecondAttemptAfterWin_ReturnsNull()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var now = DateTime.UtcNow;

        Jobs().TryClaimJob(jobId, credentialId, now, now.AddMinutes(5)).Should().NotBeNull();
        Jobs().TryClaimJob(jobId, credentialId, now, now.AddMinutes(5)).Should().BeNull();
    }

    [Fact]
    public async Task ClaimNextWorkItem_ConcurrentPollsForOneDevice_NeverHandOutAJobTwice()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        for (var i = 0; i < 5; i++) await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);

        var responses = await RunTogether(20, () =>
            Dispatch().ClaimNextWorkItem(PostgresFixture.NanoDeviceId, new ClaimJobRequest(), credentialId, PostgresFixture.NanoDeviceId));

        var claimedIds = responses.Where(x => x != null).Select(x => x!.Job!.Id).ToList();
        claimedIds.Should().HaveCount(5);
        claimedIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task StartedThenCompleted_WithTheClaimFromClaimNext_Succeeds()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = ClaimNext(credentialId).Job!;

        JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId).Should().BeTrue();
        (await StatusOf(jobId)).Should().Be("Executing");

        JobService().MarkJobCompleted(jobId, new CompleteJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId).Should().BeTrue();
        (await StatusOf(jobId)).Should().Be("Completed");
        (await _db.ScalarAsync<long>("SELECT count(*) FROM public.jobhistory WHERE jobid = @id;", ("id", jobId))).Should().Be(1);
    }

    [Fact]
    public async Task ClaimedAtUtc_SurvivesTheJsonRoundTripTheRobotMakes()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // The robot reads job.claimedAtUtc as a string and sends that same string back.
        using var claimJson = JsonDocument.Parse(JsonSerializer.Serialize(ClaimNext(credentialId), web));
        var echoed = claimJson.RootElement.GetProperty("job").GetProperty("claimedAtUtc").GetString();
        var request = JsonSerializer.Deserialize<StartJobRequest>($"{{\"claimedAtUtc\":\"{echoed}\"}}", web)!;

        JobService().MarkJobStarted(jobId, request, credentialId, PostgresFixture.NanoDeviceId).Should().BeTrue();
    }

    [Fact]
    public async Task Started_OnAJobThatWasRequeuedAfterItsLeaseExpired_IsRejected()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = ClaimNext(credentialId).Job!;

        await _db.ExpireLeaseAsync(jobId);
        Dispatch().ExpireStaleWorkForAllDevices();
        (await StatusOf(jobId)).Should().Be("Queued");

        var act = () => JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);

        act.Should().Throw<InvalidOperationException>();
        (await StatusOf(jobId)).Should().Be("Queued");
    }

    [Fact]
    public async Task Started_FromAnEarlierClaimOfTheSameJob_CannotTouchTheNewClaim()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);

        var firstClaim = ClaimNext(credentialId).Job!;
        await _db.ExpireLeaseAsync(jobId);
        Dispatch().ExpireStaleWorkForAllDevices();
        await Task.Delay(5);
        var secondClaim = ClaimNext(credentialId).Job!;

        secondClaim.Id.Should().Be(jobId);
        secondClaim.ClaimedAtUtc.Should().NotBe(firstClaim.ClaimedAtUtc);

        // Same job, same robot, same credential, status Claimed again: only claimedAtUtc tells the two claims apart.
        var stale = () => JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = firstClaim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);
        stale.Should().Throw<InvalidOperationException>();
        (await StatusOf(jobId)).Should().Be("Claimed");

        JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = secondClaim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId).Should().BeTrue();
        (await StatusOf(jobId)).Should().Be("Executing");
    }

    [Fact]
    public async Task Completed_WhileStillClaimed_IsRejected()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = ClaimNext(credentialId).Job!;

        var act = () => JobService().MarkJobCompleted(jobId, new CompleteJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);

        act.Should().Throw<InvalidOperationException>();
        (await StatusOf(jobId)).Should().Be("Claimed");
    }

    [Fact]
    public async Task Failed_WhileStillClaimed_IsAllowed_BecauseTheRobotCanRejectBeforeStarting()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = ClaimNext(credentialId).Job!;

        JobService().MarkJobFailed(jobId, new FailJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc, FailureCode = "JOB_PAYLOAD_INVALID" }, credentialId, PostgresFixture.NanoDeviceId).Should().BeTrue();
        (await StatusOf(jobId)).Should().Be("Failed");
    }

    [Fact]
    public async Task Completed_AfterTheExecutingLeaseExpired_CannotOverwriteExpired()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = ClaimNext(credentialId).Job!;
        JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);

        await _db.ExpireLeaseAsync(jobId);
        Dispatch().ExpireStaleWorkForAllDevices();
        (await StatusOf(jobId)).Should().Be("Expired");

        var act = () => JobService().MarkJobCompleted(jobId, new CompleteJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);

        act.Should().Throw<InvalidOperationException>();
        (await StatusOf(jobId)).Should().Be("Expired");
    }

    [Fact]
    public async Task StaleRequeue_FromAnOldSnapshot_CannotOverwriteAJobTheRobotAlreadyStarted()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        var claim = ClaimNext(credentialId).Job!;
        await _db.ExpireLeaseAsync(jobId);

        // The sweeper reads the job as stale and Claimed, then the robot's started lands before the sweeper writes.
        var snapshot = Jobs().GetStaleJobsByDeviceId(PostgresFixture.NanoDeviceId, DateTime.UtcNow).Single();
        JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialId, PostgresFixture.NanoDeviceId);

        Jobs().TryRequeueStaleClaimedJob(snapshot.Id, snapshot.ClaimedAtUtc, DateTime.UtcNow).Should().BeFalse();
        (await StatusOf(jobId)).Should().Be("Executing");
    }

    [Fact]
    public async Task QueuedOnlyWrite_CannotFailAJobAnotherRequestJustClaimed()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        ClaimNext(credentialId);

        Jobs().TryUpdateQueuedJobStatus(jobId, "Failed", DateTime.UtcNow).Should().BeFalse();
        (await StatusOf(jobId)).Should().Be("Claimed");
    }

    [Fact]
    public async Task RobotA_CannotOperateOnRobotBsJob()
    {
        var credentialA = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var credentialB = await _db.InsertCredentialAsync(PostgresFixture.LegacyDeviceId, "legacy-b", "secret-b");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.LegacyDeviceId);
        var claim = ClaimNext(credentialB, PostgresFixture.LegacyDeviceId).Job!;

        var start = () => JobService().MarkJobStarted(jobId, new StartJobRequest { ClaimedAtUtc = claim.ClaimedAtUtc }, credentialA, PostgresFixture.NanoDeviceId);
        var claimNext = () => Dispatch().ClaimNextWorkItem(PostgresFixture.LegacyDeviceId, new ClaimJobRequest(), credentialA, PostgresFixture.NanoDeviceId);

        start.Should().Throw<UnauthorizedAccessException>();
        claimNext.Should().Throw<InvalidOperationException>();
        (await StatusOf(jobId)).Should().Be("Claimed");
    }

    [Fact]
    public async Task Started_WithoutClaimedAtUtc_IsRejectedAsABadRequest()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        var jobId = await _db.InsertQueuedJobAsync(PostgresFixture.NanoDeviceId);
        ClaimNext(credentialId);

        var act = () => JobService().MarkJobStarted(jobId, new StartJobRequest(), credentialId, PostgresFixture.NanoDeviceId);

        act.Should().Throw<ArgumentException>();
        (await StatusOf(jobId)).Should().Be("Claimed");
    }
}
