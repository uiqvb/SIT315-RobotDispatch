using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using RobotControllerApi.Infrastructure.DataAccess.ADO;

namespace RobotControllerApi.Tests.Concurrency;

// Phase 4 F11: the claim lookup must hand out exactly the job the Phase 1-3 single-sort query did, while reading only a few rows.
[Collection("postgres")]
public class ClaimQueryTests : IAsyncLifetime
{
    private const int Device = PostgresFixture.NanoDeviceId;
    private const int OtherDevice = PostgresFixture.LegacyDeviceId;
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0);

    // The Phase 1-3 query word for word, kept as the reference answer.
    private const string ReferenceSql =
        @"SELECT j.id, j.deviceid, j.workflowid, j.stepnumber, j.commandcatalogueid, j.payloadjson, j.providertype, j.status, j.requestedbyappuserid, j.claimedbydevicecredentialid, j.claimedatutc, j.leaseexpiresatutc, j.isrollback, j.rollbackofjobhistoryid, j.createddate, j.modifieddate
              FROM public.job j
              LEFT JOIN public.workflow w ON w.id = j.workflowid
              WHERE j.deviceid = @deviceId
                AND j.status = 'Queued'
                AND (
                    j.workflowid IS NULL
                    OR (
                        w.status IN ('Queued', 'Claimed', 'Executing')
                        AND j.stepnumber IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1
                            FROM public.job earlier
                            WHERE earlier.workflowid = j.workflowid
                              AND earlier.stepnumber IS NOT NULL
                              AND earlier.stepnumber < j.stepnumber
                              AND (
                                  (w.executionmode = 'AllOrNothing' AND earlier.status <> 'Completed')
                                  OR (w.executionmode = 'BestEffort' AND earlier.status NOT IN ('Completed', 'Failed'))
                              )
                        )
                    )
                )
              ORDER BY
                COALESCE(w.createddate, j.createddate),
                CASE WHEN j.workflowid IS NULL THEN 1 ELSE 0 END,
                COALESCE(j.stepnumber, 0),
                j.createddate,
                j.id
              LIMIT 1;";

    private readonly PostgresFixture _db;

    public ClaimQueryTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync() => _db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Claim_PicksTheSameJobAsTheOldQuery_OnAHandBuiltQueue()
    {
        var s1 = await JobAsync(T0.AddSeconds(10));
        var wfB = await WorkflowAsync("Executing", "AllOrNothing", T0.AddSeconds(5));
        await JobAsync(T0.AddSeconds(6), "Completed", wfB, 1);
        var b2 = await JobAsync(T0.AddSeconds(7), "Queued", wfB, 2);
        var b3 = await JobAsync(T0.AddSeconds(8), "Queued", wfB, 3);
        var wfA = await WorkflowAsync("Queued", "BestEffort", T0.AddSeconds(20));
        var a1 = await JobAsync(T0.AddSeconds(21), "Queued", wfA, 1);
        var a2 = await JobAsync(T0.AddSeconds(22), "Queued", wfA, 2);
        var a3 = await JobAsync(T0.AddSeconds(23), "Queued", wfA, 3);
        var s2 = await JobAsync(T0.AddSeconds(30));
        var s3 = await JobAsync(T0.AddSeconds(30)); //same time as s2: the lower id goes first
        var wfD = await WorkflowAsync("Queued", "BestEffort", T0.AddSeconds(30)); //same time as s2 and s3: the workflow step goes first
        var d1 = await JobAsync(T0.AddSeconds(31), "Queued", wfD, 1);
        var wfE = await WorkflowAsync("Queued", "BestEffort", T0.AddSeconds(40));
        await JobAsync(T0.AddSeconds(41), "Failed", wfE, 1);
        var e2 = await JobAsync(T0.AddSeconds(42), "Queued", wfE, 2); //BestEffort carries on after a failed step
        var wfF = await WorkflowAsync("Queued", "AllOrNothing", T0.AddSeconds(1));
        await JobAsync(T0.AddSeconds(2), "Failed", wfF, 1);
        await JobAsync(T0.AddSeconds(3), "Queued", wfF, 2); //AllOrNothing stops after a failed step, so this is never handed out
        var wfC = await WorkflowAsync("Completed", "BestEffort", T0);
        await JobAsync(T0, "Queued", wfC, 1); //its workflow is finished, never handed out
        await JobAsync(T0, "Claimed"); //not Queued, never handed out
        await JobAsync(T0, deviceId: OtherDevice); //another robot's job, never handed out

        var picks = await DrainComparingAsync(_ => "Completed");

        picks.Should().Equal(b2, b3, s1, a1, a2, a3, d1, s2, s3, e2);
    }

    [Fact]
    public async Task Claim_PicksTheSameJobAsTheOldQuery_OnRandomQueues()
    {
        string[] workflowStatuses = ["Queued", "Claimed", "Executing", "Completed", "Failed", "Cancelled", "Expired", "RolledBack"];
        string[] jobStatuses = ["Queued", "Queued", "Queued", "Completed", "Failed", "Claimed", "Executing", "Cancelled"];
        string[] modes = ["BestEffort", "AllOrNothing"];

        for (var seed = 1; seed <= 25; seed++)
        {
            await _db.ResetAsync();
            var random = new Random(seed);
            DateTime When() => T0.AddSeconds(random.Next(0, 4)); //only 4 distinct times, so ties are common

            for (var i = random.Next(0, 12); i > 0; i--)
            {
                await JobAsync(When(), jobStatuses[random.Next(jobStatuses.Length)], step: random.Next(3) == 0 ? random.Next(1, 4) : null,
                    deviceId: random.Next(5) == 0 ? OtherDevice : Device);
            }

            for (var w = random.Next(0, 5); w > 0; w--)
            {
                var workflowId = await WorkflowAsync(workflowStatuses[random.Next(workflowStatuses.Length)], modes[random.Next(modes.Length)], When());
                var steps = random.Next(1, 5);
                for (var s = 0; s < steps; s++)
                {
                    await JobAsync(When(), jobStatuses[random.Next(jobStatuses.Length)], workflowId, random.Next(8) == 0 ? null : random.Next(1, 5),
                        random.Next(10) == 0 ? OtherDevice : Device); //duplicate, missing and out-of-order step numbers on purpose
                }
            }

            await DrainComparingAsync(_ => random.Next(4) == 0 ? "Failed" : "Completed");
        }
    }

    [Fact]
    public async Task Claim_ReadsAFewRows_NotTheWholeQueue()
    {
        await _db.ExecuteAsync(
            @"INSERT INTO public.job (deviceid, commandcatalogueid, status, createddate, modifieddate)
              SELECT @deviceId, @command, 'Queued', @start + n * INTERVAL '1 millisecond', @start
              FROM generate_series(0, 1999) AS n;",
            ("deviceId", Device), ("command", PostgresFixture.StopCommandId), ("start", T0));
        await _db.ExecuteAsync("ANALYZE public.job;");
        await _db.ExecuteAsync("ANALYZE public.workflow;");

        var claimSql = (string)typeof(JobADO).GetField("GetOldestQueuedJobByDeviceIdSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!; //the exact SQL the API runs

        (await JobRowsReadAsync(ReferenceSql)).Should().BeGreaterThanOrEqualTo(2000); //the old query reads the robot's whole queue
        (await JobRowsReadAsync(claimSql)).Should().BeLessThan(10); //the new one stops at the front of it
    }

    // Asks both queries, checks they agree, settles the pick, and repeats until both say the queue is empty.
    private async Task<List<int>> DrainComparingAsync(Func<int, string> settleAs)
    {
        var picks = new List<int>();
        for (var round = 1; round <= 500; round++)
        {
            var expected = await ReferencePickAsync();
            var actual = (await new JobADO(_db.DbConfig).GetOldestQueuedJobByDeviceIdAsync(Device))?.Id;
            actual.Should().Be(expected, $"pick {round} must match the reference query");
            if (expected == null) return picks;

            picks.Add(expected.Value);
            await _db.ExecuteAsync("UPDATE public.job SET status = @status WHERE id = @id;", ("status", settleAs(expected.Value)), ("id", expected.Value));
        }

        throw new InvalidOperationException("The queue never drained.");
    }

    private async Task<int?> ReferencePickAsync()
    {
        await using var conn = new NpgsqlConnection(_db.DbConfig.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(ReferenceSql, conn);
        cmd.Parameters.AddWithValue("deviceId", Device);
        return (int?)await cmd.ExecuteScalarAsync();
    }

    private Task<int> WorkflowAsync(string status, string mode, DateTime created) => _db.ScalarAsync<int>(
        @"INSERT INTO public.workflow (deviceid, name, executionmode, status, createddate, modifieddate)
          VALUES (@deviceId, 'test workflow', @mode, @status, @created, @created)
          RETURNING id;",
        ("deviceId", Device), ("mode", mode), ("status", status), ("created", created));

    private Task<int> JobAsync(DateTime created, string status = "Queued", int? workflowId = null, int? step = null, int deviceId = Device) => _db.ScalarAsync<int>(
        @"INSERT INTO public.job (deviceid, workflowid, stepnumber, commandcatalogueid, status, createddate, modifieddate)
          VALUES (@deviceId, @workflowId, @step, @command, @status, @created, @created)
          RETURNING id;",
        ("deviceId", deviceId), ("workflowId", (object?)workflowId ?? DBNull.Value), ("step", (object?)step ?? DBNull.Value),
        ("command", PostgresFixture.StopCommandId), ("status", status), ("created", created));

    // Rows PostgreSQL touched in the job table to answer one claim, read from EXPLAIN ANALYZE.
    private async Task<long> JobRowsReadAsync(string sql)
    {
        var json = await _db.ScalarAsync<string>("EXPLAIN (ANALYZE, FORMAT JSON) " + sql.Replace("@deviceId", Device.ToString()));
        using var plan = JsonDocument.Parse(json);
        return (long)Math.Round(RowsRead(plan.RootElement[0].GetProperty("Plan")));
    }

    private static double RowsRead(JsonElement node)
    {
        var rows = 0.0;
        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "job")
        {
            var removed = node.TryGetProperty("Rows Removed by Filter", out var filtered) ? filtered.GetDouble() : 0;
            rows += (node.GetProperty("Actual Rows").GetDouble() + removed) * node.GetProperty("Actual Loops").GetDouble(); //per-loop averages times loops
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray()) rows += RowsRead(child);
        }

        return rows;
    }
}
