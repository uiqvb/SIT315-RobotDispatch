using Microsoft.Extensions.Configuration;
using Npgsql;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Services;

namespace RobotLoadGen;

// The throwaway benchmark database: schema load, per-run reset + seed, and the after-run check.
public sealed class BenchDatabase(string connectionString)
{
    public const int FirstDeviceId = 1001; //robot i drives device 1001 + i; the two reference devices (1, 2) are never touched
    private const int MapId = 1; //the seeded 10x10 grid

    public static string CredentialIdentifier(int deviceId) => $"bench-cred-{deviceId}";

    public static string Secret(int deviceId) => $"rc_bench_{deviceId}_loadgen_secret";

    public async Task InitAsync(string repoRoot)
    {
        await WaitUntilReachableAsync();
        var folder = Path.Combine(repoRoot, "Infrastructure", "DataAccess", "Database");
        await using var conn = await OpenAsync();
        await ExecuteAsync(conn, await File.ReadAllTextAsync(Path.Combine(folder, "Final_project_schema_refactored.sql"))); //same files the tests load
        await ExecuteAsync(conn, await File.ReadAllTextAsync(Path.Combine(folder, "Final_project_reference_data_refactored.sql")));
        Console.WriteLine("Benchmark database loaded with the repo's schema and reference data.");
    }

    public async Task SeedAsync(int robots, int backlog, string hashKey, bool allowAnyDatabase = false)
    {
        var hasher = new MultiDeviceCredentialSecretHashService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DeviceCredential:HashKey"] = hashKey })
            .Build()); //the API's own hasher, so the stored hash is exactly what the API will verify

        await using var conn = await OpenAsync();
        await RefuseNonBenchDatabaseAsync(conn, allowAnyDatabase);
        await using (var tx = await conn.BeginTransactionAsync())
        {
            // Every run starts from the same state: no jobs, no history, no bench robots.
            await ExecuteAsync(conn, "TRUNCATE public.jobhistory, public.workflowhistory, public.job, public.workflow, public.devicecredential RESTART IDENTITY CASCADE;", tx);
            await ExecuteAsync(conn, "DELETE FROM public.devicecapability WHERE deviceid >= @first;", tx, ("first", FirstDeviceId));
            await ExecuteAsync(conn, "DELETE FROM public.devicestatus WHERE deviceid >= @first;", tx, ("first", FirstDeviceId));
            await ExecuteAsync(conn, "DELETE FROM public.device WHERE id >= @first;", tx, ("first", FirstDeviceId));

            await ExecuteAsync(conn,
                @"INSERT INTO public.device (id, name, deviceidentifier, devicetype, mapid, description, isactive)
                  SELECT @first + n, 'Bench Robot ' || (@first + n), 'bench_robot_' || (@first + n), 'TwoWheelDriveCar', @map, 'Simulated robot for the load generator.', true
                  FROM generate_series(0, @robots - 1) AS n;", tx, ("first", FirstDeviceId), ("map", MapId), ("robots", robots));

            // Trusted pose at (5,5) facing North, so grid jobs pass the claim-time pose check.
            await ExecuteAsync(conn,
                @"INSERT INTO public.devicestatus (deviceid, connectionstate, operationalstate, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, isinsidemap, statusmessage)
                  SELECT @first + n, 'Online', 'Idle', @map, 5, 5, 'North', true, true, 1.0, true, 'Seeded by the load generator at a trusted pose.'
                  FROM generate_series(0, @robots - 1) AS n;", tx, ("first", FirstDeviceId), ("map", MapId), ("robots", robots));

            // Same grid capabilities as the real Nano, so claim-next builds the inverse command too.
            await ExecuteAsync(conn,
                @"INSERT INTO public.devicecapability (deviceid, commandcatalogueid, requiresmap, description, isactive)
                  SELECT d.id, cc.id, true, 'Grid capability for a simulated robot.', true
                  FROM public.device d CROSS JOIN public.commandcatalogue cc
                  WHERE d.id >= @first AND cc.name IN ('PLACE', 'MOVE', 'LEFT', 'RIGHT', 'STEP_BACK');", tx, ("first", FirstDeviceId));

            for (var i = 0; i < robots; i++)
            {
                var deviceId = FirstDeviceId + i;
                var secret = Secret(deviceId);
                await ExecuteAsync(conn,
                    @"INSERT INTO public.devicecredential (deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, isactive)
                      VALUES (@deviceId, @identifier, @identifier, @prefix, @hash, @algorithm, true);", tx,
                    ("deviceId", deviceId), ("identifier", CredentialIdentifier(deviceId)), ("prefix", hasher.GetSecretPrefix(secret)),
                    ("hash", hasher.HashSecret(secret)), ("algorithm", hasher.GetCurrentAlgorithm()));
            }

            // The backlog: MOVE, MOVE, LEFT, LEFT repeated, so each robot walks 2 cells, turns round, and never leaves the map.
            // createddate rises by 1 ms per job, so claim-next hands them out in exactly this order.
            await ExecuteAsync(conn,
                @"WITH cmd AS (
                      SELECT (SELECT id FROM public.commandcatalogue WHERE name = 'MOVE') AS move_id,
                             (SELECT id FROM public.commandcatalogue WHERE name = 'LEFT') AS left_id)
                  INSERT INTO public.job (deviceid, commandcatalogueid, payloadjson, providertype, status, createddate, modifieddate)
                  SELECT d.id,
                         CASE WHEN s.n % 4 < 2 THEN cmd.move_id ELSE cmd.left_id END,
                         '{}'::jsonb, 'Api', 'Queued',
                         TIMESTAMP '2026-01-01 00:00:00' + s.n * INTERVAL '1 millisecond',
                         TIMESTAMP '2026-01-01 00:00:00' + s.n * INTERVAL '1 millisecond'
                  FROM public.device d CROSS JOIN generate_series(0, @backlog - 1) AS s(n) CROSS JOIN cmd
                  WHERE d.id >= @first
                  ORDER BY d.id, s.n;", tx, ("first", FirstDeviceId), ("backlog", backlog));

            await tx.CommitAsync();
        }

        await ExecuteAsync(conn, "ANALYZE;"); //fresh planner statistics, so both versions start with the same query plans
        Console.WriteLine($"Seeded {robots} robots x {backlog} queued jobs = {robots * backlog} jobs.");
    }

    public async Task CheckAsync(string label, int robots, string outPath)
    {
        await using var conn = await OpenAsync();

        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new NpgsqlCommand("SELECT status, count(*) FROM public.job WHERE deviceid >= @first GROUP BY status;", conn))
        {
            cmd.Parameters.AddWithValue("first", FirstDeviceId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        await using var historyCmd = new NpgsqlCommand("SELECT count(*) FROM public.jobhistory;", conn);
        var historyRows = (long)(await historyCmd.ExecuteScalarAsync())!;

        long Count(string status) => counts.TryGetValue(status, out var n) ? n : 0;
        var known = new[] { "Queued", "Claimed", "Executing", "Completed", "Failed" };
        var other = counts.Where(x => !known.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).Sum(x => x.Value);

        var newFile = !File.Exists(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await using var writer = new StreamWriter(outPath, append: true);
        if (newFile) await writer.WriteLineAsync("label,robots,queued,claimed,executing,completed,failed,other,jobhistory_rows,checked_at_utc");
        await writer.WriteLineAsync(string.Join(",", label, robots, Count("Queued"), Count("Claimed"), Count("Executing"), Count("Completed"),
            Count("Failed"), other, historyRows, DateTime.UtcNow.ToString("O")));

        Console.WriteLine($"DB check {label}/{robots}: completed {Count("Completed")}, failed {Count("Failed")}, " +
                          $"claimed {Count("Claimed")}, executing {Count("Executing")}, queued {Count("Queued")}, other {other}, history rows {historyRows}");
    }

    // Seeding deletes every job, history and credential row, so it only runs on a database whose name says it is a bench.
    private static async Task RefuseNonBenchDatabaseAsync(NpgsqlConnection conn, bool allowAnyDatabase)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_database();", conn);
        var name = (string)(await cmd.ExecuteScalarAsync())!;
        if (!allowAnyDatabase && !name.Contains("bench", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to seed '{name}': seeding deletes every job, history and credential row. Use a database whose name contains 'bench', or pass --allow-any-db yes.");
        }
    }

    private async Task WaitUntilReachableAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await OpenAsync();
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                await Task.Delay(1000); //the container's first start runs initdb, so the port can take a few seconds to answer
            }
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
