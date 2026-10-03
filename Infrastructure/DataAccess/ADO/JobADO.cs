using Npgsql;
using NpgsqlTypes;
using RobotControllerApi.BoundedContexts.Jobs.Models;
using RobotControllerApi.BoundedContexts.Jobs.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class JobADO : IJobDataAccess
{
    private readonly DbConfig _dbConfig;

    public JobADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<Job> GetJobs()
    {
        var results = new List<Job>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJob(dr));
        }

        return results;
    }

    public Job? GetJobById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapJob(dr) : null;
    }

    public List<Job> GetJobsByDeviceId(int deviceId)
    {
        var results = new List<Job>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE deviceid = @deviceId
              ORDER BY id;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJob(dr));
        }

        return results;
    }

    public List<Job> GetJobsByWorkflowId(int workflowId)
    {
        var results = new List<Job>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE workflowid = @workflowId
              ORDER BY stepnumber, id;", conn);

        cmd.Parameters.AddWithValue("workflowId", workflowId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJob(dr));
        }

        return results;
    }

    public Job? GetOldestQueuedStandaloneJobByDeviceId(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE deviceid = @deviceId AND workflowid IS NULL AND status = 'Queued'
              ORDER BY createddate, id
              LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapJob(dr) : null;
    }


    public Job? GetOldestQueuedJobByDeviceId(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
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
              LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapJob(dr) : null;
    }

    public Job InsertJob(Job newJob)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.job
              (deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate)
              VALUES (@deviceId, @workflowId, @stepNumber, @commandCatalogueId, @payloadJson::jsonb, @providerType, @status, @requestedByAppUserId, @claimedByDeviceCredentialId, @claimedAtUtc, @leaseExpiresAtUtc, @isRollback, @rollbackOfJobHistoryId, @createdDate, @modifiedDate)
              RETURNING id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate;", conn);

        AddParameters(cmd, newJob);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapJob(dr);
        }

        throw new InvalidOperationException("Job insert failed.");
    }

    public bool UpdateJob(int id, Job updatedJob)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"UPDATE public.job
              SET deviceid = @deviceId,
                  workflowid = @workflowId,
                  stepnumber = @stepNumber,
                  commandcatalogueid = @commandCatalogueId,
                  payloadjson = @payloadJson::jsonb,
                  providertype = @providerType,
                  status = @status,
                  requestedbyappuserid = @requestedByAppUserId,
                  claimedbydevicecredentialid = @claimedByDeviceCredentialId,
                  claimedatutc = @claimedAtUtc,
                  leaseexpiresatutc = @leaseExpiresAtUtc,
                  isrollback = @isRollback,
                  rollbackofjobhistoryid = @rollbackOfJobHistoryId,
                  modifieddate = @modifiedDate
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);
        AddParameters(cmd, updatedJob);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteJob(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"DELETE FROM public.job
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeviceExistsAndActive(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.device
                  WHERE id = @deviceId AND isactive = true
              );", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        return (bool)(cmd.ExecuteScalar() ?? false);
    }

    public int? GetDeviceMapId(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT mapid
              FROM public.device
              WHERE id = @deviceId;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
    }

    public bool CommandCatalogueExistsAndActive(int commandCatalogueId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.commandcatalogue
                  WHERE id = @commandCatalogueId AND isactive = true
              );", conn);

        cmd.Parameters.AddWithValue("commandCatalogueId", commandCatalogueId);

        return (bool)(cmd.ExecuteScalar() ?? false);
    }

    public string? GetCommandCatalogueNameById(int commandCatalogueId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT name
              FROM public.commandcatalogue
              WHERE id = @commandCatalogueId;", conn);

        cmd.Parameters.AddWithValue("commandCatalogueId", commandCatalogueId);

        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    public CommandCatalogueSnapshot? GetCommandCatalogueById(int commandCatalogueId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, name, executionkind, rollbackkind, inversecommandname, requiresduration, isactive
              FROM public.commandcatalogue
              WHERE id = @commandCatalogueId;", conn);

        cmd.Parameters.AddWithValue("commandCatalogueId", commandCatalogueId);

        using var dr = cmd.ExecuteReader();
        if (!dr.Read()) return null;

        return new CommandCatalogueSnapshot
        {
            Id = dr.GetInt32(0),
            Name = dr.GetString(1),
            ExecutionKind = dr.GetString(2),
            RollbackKind = dr.GetString(3),
            InverseCommandName = dr.IsDBNull(4) ? null : dr.GetString(4),
            RequiresDuration = dr.GetBoolean(5),
            IsActive = dr.GetBoolean(6)
        };
    }

    public int? GetCommandCatalogueIdByName(string commandName)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id
              FROM public.commandcatalogue
              WHERE lower(name) = lower(@commandName) AND isactive = true;", conn);

        cmd.Parameters.AddWithValue("commandName", commandName);

        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
    }

    public DeviceCapabilitySnapshot? GetActiveDeviceCapability(int deviceId, int commandCatalogueId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, commandcatalogueid, requiresmap, isactive
              FROM public.devicecapability
              WHERE deviceid = @deviceId AND commandcatalogueid = @commandCatalogueId AND isactive = true;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("commandCatalogueId", commandCatalogueId);

        using var dr = cmd.ExecuteReader();

        if (!dr.Read()) return null;

        return new DeviceCapabilitySnapshot
        {
            Id = dr.GetInt32(0),
            DeviceId = dr.GetInt32(1),
            CommandCatalogueId = dr.GetInt32(2),
            RequiresMap = dr.GetBoolean(3),
            IsActive = dr.GetBoolean(4)
        };
    }

    public bool IsDeviceGridPoseTrustedAndAligned(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.devicestatus
                  WHERE deviceid = @deviceId
                    AND isgridposetrusted = true
                    AND isgridaligned = true
              );", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        return (bool)(cmd.ExecuteScalar() ?? false);
    }

    public bool DeviceCredentialOwnsDevice(int deviceCredentialId, int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.devicecredential dc
                  JOIN public.device d ON d.id = dc.deviceid
                  WHERE dc.id = @deviceCredentialId
                    AND dc.deviceid = @deviceId
                    AND dc.isactive = true
                    AND dc.revokedatutc IS NULL
                    AND (dc.expiresatutc IS NULL OR dc.expiresatutc > (now() AT TIME ZONE 'utc'))
                    AND d.isactive = true
              );", conn);

        cmd.Parameters.AddWithValue("deviceCredentialId", deviceCredentialId);
        cmd.Parameters.AddWithValue("deviceId", deviceId);

        return (bool)(cmd.ExecuteScalar() ?? false);
    }

    private const string JobColumns = "id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate";

    public List<Job> GetStaleJobsByDeviceId(int deviceId, DateTime now)
    {
        var results = new List<Job>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        // Filters in SQL, so only stale rows cross the wire instead of the device's whole job history.
        using var cmd = new NpgsqlCommand(
            $@"SELECT {JobColumns}
              FROM public.job
              WHERE deviceid = @deviceId
                AND status IN ('Claimed', 'Executing')
                AND leaseexpiresatutc IS NOT NULL
                AND leaseexpiresatutc <= @now
              ORDER BY createddate, id;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("now", now);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJob(dr));
        }

        return results;
    }

    public Job? TryClaimJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime leaseExpiresAtUtc)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        // Only one concurrent caller can match status = 'Queued'; every other caller gets zero rows.
        using var cmd = new NpgsqlCommand(
            $@"UPDATE public.job
              SET status = 'Claimed',
                  claimedbydevicecredentialid = @deviceCredentialId,
                  claimedatutc = @claimedAtUtc,
                  leaseexpiresatutc = @leaseExpiresAtUtc,
                  modifieddate = @claimedAtUtc
              WHERE id = @id
                AND status = 'Queued'
              RETURNING {JobColumns};", conn);

        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("deviceCredentialId", deviceCredentialId);
        cmd.Parameters.AddWithValue("claimedAtUtc", claimedAtUtc);
        cmd.Parameters.AddWithValue("leaseExpiresAtUtc", leaseExpiresAtUtc);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapJob(dr) : null;
    }

    public bool TryUpdateQueuedJobStatus(int jobId, string newStatus, DateTime modifiedDate)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        // Used for cancel and validation-fail writes, which must never land on a job someone already claimed.
        using var cmd = new NpgsqlCommand(
            @"UPDATE public.job
              SET status = @newStatus,
                  modifieddate = @modifiedDate
              WHERE id = @id
                AND status = 'Queued';", conn);

        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("newStatus", newStatus);
        cmd.Parameters.AddWithValue("modifiedDate", modifiedDate);

        return cmd.ExecuteNonQuery() > 0;
    }

    public Job? TryMarkClaimedJobExecuting(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime modifiedDate)
    {
        //started is the same guarded update, from Claimed only
        return TryFinishClaimedJob(jobId, deviceCredentialId, claimedAtUtc, new[] { "Claimed" }, "Executing", modifiedDate);
    }

    public Job? TryFinishClaimedJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, string[] fromStatuses, string newStatus, DateTime modifiedDate)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        // claimedatutc pins the exact claim, so a late request from an earlier claim of the same job matches nothing.
        using var cmd = new NpgsqlCommand(
            $@"UPDATE public.job
              SET status = @newStatus,
                  modifieddate = @modifiedDate
              WHERE id = @id
                AND status = ANY(@fromStatuses)
                AND claimedbydevicecredentialid = @deviceCredentialId
                AND claimedatutc = @claimedAtUtc
              RETURNING {JobColumns};", conn);

        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("newStatus", newStatus);
        cmd.Parameters.AddWithValue("modifiedDate", modifiedDate);
        cmd.Parameters.AddWithValue("fromStatuses", fromStatuses);
        cmd.Parameters.AddWithValue("deviceCredentialId", deviceCredentialId);
        cmd.Parameters.AddWithValue("claimedAtUtc", claimedAtUtc);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapJob(dr) : null;
    }

    public bool TryRequeueStaleClaimedJob(int jobId, DateTime? claimedAtUtc, DateTime now)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        // Requeues only if the row is still the same claim and its lease really has passed.
        using var cmd = new NpgsqlCommand(
            @"UPDATE public.job
              SET status = 'Queued',
                  claimedbydevicecredentialid = NULL,
                  claimedatutc = NULL,
                  leaseexpiresatutc = NULL,
                  modifieddate = @now
              WHERE id = @id
                AND status = 'Claimed'
                AND claimedatutc IS NOT DISTINCT FROM @claimedAtUtc
                AND leaseexpiresatutc <= @now;", conn);

        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.Add(new NpgsqlParameter("claimedAtUtc", NpgsqlDbType.Timestamp) { Value = (object?)claimedAtUtc ?? DBNull.Value });
        cmd.Parameters.AddWithValue("now", now);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool TryExpireStaleExecutingJob(int jobId, DateTime? claimedAtUtc, DateTime now)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        // Expires only if the robot has not completed or failed this claim in the meantime.
        using var cmd = new NpgsqlCommand(
            @"UPDATE public.job
              SET status = 'Expired',
                  modifieddate = @now
              WHERE id = @id
                AND status = 'Executing'
                AND claimedatutc IS NOT DISTINCT FROM @claimedAtUtc
                AND leaseexpiresatutc <= @now;", conn);

        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.Add(new NpgsqlParameter("claimedAtUtc", NpgsqlDbType.Timestamp) { Value = (object?)claimedAtUtc ?? DBNull.Value });
        cmd.Parameters.AddWithValue("now", now);

        return cmd.ExecuteNonQuery() > 0;
    }

    private static void AddParameters(NpgsqlCommand cmd, Job model)
    {
        cmd.Parameters.AddWithValue("deviceId", model.DeviceId);
        cmd.Parameters.AddWithValue("workflowId", model.WorkflowId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("stepNumber", model.StepNumber ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("commandCatalogueId", model.CommandCatalogueId);
        cmd.Parameters.AddWithValue("payloadJson", model.PayloadJson);
        cmd.Parameters.AddWithValue("providerType", model.ProviderType);
        cmd.Parameters.AddWithValue("status", model.Status);
        cmd.Parameters.AddWithValue("requestedByAppUserId", model.RequestedByAppUserId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("claimedByDeviceCredentialId", model.ClaimedByDeviceCredentialId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("claimedAtUtc", model.ClaimedAtUtc ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("leaseExpiresAtUtc", model.LeaseExpiresAtUtc ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isRollback", model.IsRollback);
        cmd.Parameters.AddWithValue("rollbackOfJobHistoryId", model.RollbackOfJobHistoryId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("createdDate", model.CreatedDate);
        cmd.Parameters.AddWithValue("modifiedDate", model.ModifiedDate);
    }

    private static Job MapJob(NpgsqlDataReader dr)
    {
        return new Job
        {
            Id = dr.GetInt32(0),
            DeviceId = dr.GetInt32(1),
            WorkflowId = dr.IsDBNull(2) ? null : dr.GetInt32(2),
            StepNumber = dr.IsDBNull(3) ? null : dr.GetInt32(3),
            CommandCatalogueId = dr.GetInt32(4),
            PayloadJson = dr.GetString(5),
            ProviderType = dr.GetString(6),
            Status = dr.GetString(7),
            RequestedByAppUserId = dr.IsDBNull(8) ? null : dr.GetInt32(8),
            ClaimedByDeviceCredentialId = dr.IsDBNull(9) ? null : dr.GetInt32(9),
            ClaimedAtUtc = dr.IsDBNull(10) ? null : dr.GetDateTime(10),
            LeaseExpiresAtUtc = dr.IsDBNull(11) ? null : dr.GetDateTime(11),
            IsRollback = dr.GetBoolean(12),
            RollbackOfJobHistoryId = dr.IsDBNull(13) ? null : dr.GetInt32(13),
            CreatedDate = dr.GetDateTime(14),
            ModifiedDate = dr.GetDateTime(15)
        };
    }
}
