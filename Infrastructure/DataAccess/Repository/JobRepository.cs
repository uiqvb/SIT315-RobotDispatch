using Npgsql;
using RobotControllerApi.BoundedContexts.Jobs.Models;
using RobotControllerApi.BoundedContexts.Jobs.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class JobRepository : IJobDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public JobRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<Job> GetJobs()
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              ORDER BY id;");
    }

    public Job? GetJobById(int id)
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE id = @id;",
            new NpgsqlParameter[]
            {
                new("id", id)
            })
            .SingleOrDefault();
    }

    public List<Job> GetJobsByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE deviceid = @deviceId
              ORDER BY id;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            });
    }

    public List<Job> GetJobsByWorkflowId(int workflowId)
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE workflowid = @workflowId
              ORDER BY stepnumber, id;",
            new NpgsqlParameter[]
            {
                new("workflowId", workflowId)
            });
    }

    public Job? GetOldestQueuedStandaloneJobByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
            @"SELECT id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate
              FROM public.job
              WHERE deviceid = @deviceId AND workflowid IS NULL AND status = 'Queued'
              ORDER BY createddate, id
              LIMIT 1;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            })
            .FirstOrDefault();
    }


    public Job? GetOldestQueuedJobByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
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
              LIMIT 1;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            })
            .FirstOrDefault();
    }

    public Job InsertJob(Job newJob)
    {
        return _repo.ExecuteReader<Job>(
            ConnectionString,
            @"INSERT INTO public.job
              (deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate)
              VALUES (@deviceId, @workflowId, @stepNumber, @commandCatalogueId, @payloadJson, @providerType, @status, @requestedByAppUserId, @claimedByDeviceCredentialId, @claimedAtUtc, @leaseExpiresAtUtc, @isRollback, @rollbackOfJobHistoryId, @createdDate, @modifiedDate)
              RETURNING id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate;",
            BuildParameters(newJob))
            .Single();
    }

    public bool UpdateJob(int id, Job updatedJob)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("id", id)
        };

        parameters.AddRange(BuildParameters(updatedJob));

        var result = _repo.ExecuteReader<Job>(
            ConnectionString,
            @"UPDATE public.job
              SET deviceid = @deviceId,
                  workflowid = @workflowId,
                  stepnumber = @stepNumber,
                  commandcatalogueid = @commandCatalogueId,
                  payloadjson = @payloadJson,
                  providertype = @providerType,
                  status = @status,
                  requestedbyappuserid = @requestedByAppUserId,
                  claimedbydevicecredentialid = @claimedByDeviceCredentialId,
                  claimedatutc = @claimedAtUtc,
                  leaseexpiresatutc = @leaseExpiresAtUtc,
                  isrollback = @isRollback,
                  rollbackofjobhistoryid = @rollbackOfJobHistoryId,
                  modifieddate = @modifiedDate
              WHERE id = @id
              RETURNING id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate;",
            parameters.ToArray());

        return result.Any();
    }

    public bool DeleteJob(int id)
    {
        var result = _repo.ExecuteReader<Job>(
            ConnectionString,
            @"DELETE FROM public.job
              WHERE id = @id
              RETURNING id, deviceid, workflowid, stepnumber, commandcatalogueid, payloadjson, providertype, status, requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc, isrollback, rollbackofjobhistoryid, createddate, modifieddate;",
            new NpgsqlParameter[]
            {
                new("id", id)
            });

        return result.Any();
    }

    public bool DeviceExistsAndActive(int deviceId)
    {
        return ReadBool(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.device
                  WHERE id = @deviceId AND isactive = true
              ) AS value;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            });
    }

    public int? GetDeviceMapId(int deviceId)
    {
        return _repo.ExecuteReader<IntResult>(
            ConnectionString,
            @"SELECT mapid AS value
              FROM public.device
              WHERE id = @deviceId;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            })
            .FirstOrDefault()?.Value;
    }

    public bool CommandCatalogueExistsAndActive(int commandCatalogueId)
    {
        return ReadBool(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.commandcatalogue
                  WHERE id = @commandCatalogueId AND isactive = true
              ) AS value;",
            new NpgsqlParameter[]
            {
                new("commandCatalogueId", commandCatalogueId)
            });
    }

    public string? GetCommandCatalogueNameById(int commandCatalogueId)
    {
        return _repo.ExecuteReader<StringResult>(
            ConnectionString,
            @"SELECT name AS value
              FROM public.commandcatalogue
              WHERE id = @commandCatalogueId;",
            new NpgsqlParameter[]
            {
                new("commandCatalogueId", commandCatalogueId)
            })
            .FirstOrDefault()?.Value;
    }

    public CommandCatalogueSnapshot? GetCommandCatalogueById(int commandCatalogueId)
    {
        return _repo.ExecuteReader<CommandCatalogueSnapshot>(
            ConnectionString,
            @"SELECT id, name, executionkind, rollbackkind, inversecommandname, requiresduration, isactive
              FROM public.commandcatalogue
              WHERE id = @commandCatalogueId;",
            new NpgsqlParameter[]
            {
                new("commandCatalogueId", commandCatalogueId)
            })
            .FirstOrDefault();
    }

    public int? GetCommandCatalogueIdByName(string commandName)
    {
        return _repo.ExecuteReader<IntResult>(
            ConnectionString,
            @"SELECT id AS value
              FROM public.commandcatalogue
              WHERE lower(name) = lower(@commandName) AND isactive = true;",
            new NpgsqlParameter[]
            {
                new("commandName", commandName)
            })
            .FirstOrDefault()?.Value;
    }

    public DeviceCapabilitySnapshot? GetActiveDeviceCapability(int deviceId, int commandCatalogueId)
    {
        return _repo.ExecuteReader<DeviceCapabilitySnapshot>(
            ConnectionString,
            @"SELECT id, deviceid, commandcatalogueid, requiresmap, isactive
              FROM public.devicecapability
              WHERE deviceid = @deviceId AND commandcatalogueid = @commandCatalogueId AND isactive = true;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId),
                new("commandCatalogueId", commandCatalogueId)
            })
            .FirstOrDefault();
    }

    public bool IsDeviceGridPoseTrustedAndAligned(int deviceId)
    {
        return ReadBool(
            @"SELECT EXISTS (
                  SELECT 1
                  FROM public.devicestatus
                  WHERE deviceid = @deviceId
                    AND isgridposetrusted = true
                    AND isgridaligned = true
              ) AS value;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            });
    }

    public bool DeviceCredentialOwnsDevice(int deviceCredentialId, int deviceId)
    {
        return ReadBool(
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
              ) AS value;",
            new NpgsqlParameter[]
            {
                new("deviceCredentialId", deviceCredentialId),
                new("deviceId", deviceId)
            });
    }

    private bool ReadBool(string sql, NpgsqlParameter[] parameters)
    {
        return _repo.ExecuteReader<BoolResult>(ConnectionString, sql, parameters).FirstOrDefault()?.Value ?? false;
    }

    private static NpgsqlParameter[] BuildParameters(Job model)
    {
        return new[]
        {
            new NpgsqlParameter("deviceId", model.DeviceId),
            new NpgsqlParameter("workflowId", model.WorkflowId ?? (object)DBNull.Value),
            new NpgsqlParameter("stepNumber", model.StepNumber ?? (object)DBNull.Value),
            new NpgsqlParameter("commandCatalogueId", model.CommandCatalogueId),
            new NpgsqlParameter("payloadJson", model.PayloadJson),
            new NpgsqlParameter("providerType", model.ProviderType),
            new NpgsqlParameter("status", model.Status),
            new NpgsqlParameter("requestedByAppUserId", model.RequestedByAppUserId ?? (object)DBNull.Value),
            new NpgsqlParameter("claimedByDeviceCredentialId", model.ClaimedByDeviceCredentialId ?? (object)DBNull.Value),
            new NpgsqlParameter("claimedAtUtc", model.ClaimedAtUtc ?? (object)DBNull.Value),
            new NpgsqlParameter("leaseExpiresAtUtc", model.LeaseExpiresAtUtc ?? (object)DBNull.Value),
            new NpgsqlParameter("isRollback", model.IsRollback),
            new NpgsqlParameter("rollbackOfJobHistoryId", model.RollbackOfJobHistoryId ?? (object)DBNull.Value),
            new NpgsqlParameter("createdDate", model.CreatedDate),
            new NpgsqlParameter("modifiedDate", model.ModifiedDate)
        };
    }

    // SIT315 guarded dispatch transitions are implemented for the ADO provider only.
    private const string AdoOnly = "Guarded dispatch transitions are only implemented for the ADO persistence provider.";
    public List<Job> GetStaleJobsByDeviceId(int deviceId, DateTime now) => throw new NotSupportedException(AdoOnly);
    public List<Job> GetStaleJobs(DateTime now) => throw new NotSupportedException(AdoOnly);
    public Job? TryClaimJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime leaseExpiresAtUtc) => throw new NotSupportedException(AdoOnly);
    public bool TryUpdateQueuedJobStatus(int jobId, string newStatus, DateTime modifiedDate) => throw new NotSupportedException(AdoOnly);
    public Job? TryMarkClaimedJobExecuting(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime modifiedDate) => throw new NotSupportedException(AdoOnly);
    public Job? TryFinishClaimedJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, string[] fromStatuses, string newStatus, DateTime modifiedDate) => throw new NotSupportedException(AdoOnly);
    public bool TryRequeueStaleClaimedJob(int jobId, DateTime? claimedAtUtc, DateTime now) => throw new NotSupportedException(AdoOnly);
    public bool TryExpireStaleExecutingJob(int jobId, DateTime? claimedAtUtc, DateTime now) => throw new NotSupportedException(AdoOnly);

    private class BoolResult { public bool Value { get; set; } }
    private class IntResult { public int? Value { get; set; } }
    private class StringResult { public string? Value { get; set; } }
}
