using Microsoft.EntityFrameworkCore;
using Npgsql;
using RobotControllerApi.BoundedContexts.Jobs.Models;
using RobotControllerApi.BoundedContexts.Jobs.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class JobEF : IJobDataAccess
{
    private readonly RobotContext _context;

    public JobEF(RobotContext context)
    {
        _context = context;
    }

    public List<Job> GetJobs()
    {
        return _context.Jobs
            .OrderBy(x => x.Id)
            .ToList();
    }

    public Job? GetJobById(int id)
    {
        return _context.Jobs
            .SingleOrDefault(x => x.Id == id);
    }

    public List<Job> GetJobsByDeviceId(int deviceId)
    {
        return _context.Jobs
            .Where(x => x.DeviceId == deviceId)
            .OrderBy(x => x.Id)
            .ToList();
    }

    public List<Job> GetJobsByWorkflowId(int workflowId)
    {
        return _context.Jobs
            .Where(x => x.WorkflowId == workflowId)
            .OrderBy(x => x.StepNumber)
            .ThenBy(x => x.Id)
            .ToList();
    }

    public Job? GetOldestQueuedStandaloneJobByDeviceId(int deviceId)
    {
        return _context.Jobs
            .Where(x => x.DeviceId == deviceId && x.WorkflowId == null && x.Status == "Queued")
            .OrderBy(x => x.CreatedDate)
            .ThenBy(x => x.Id)
            .FirstOrDefault();
    }


    public Job? GetOldestQueuedJobByDeviceId(int deviceId)
    {
        return _context.Jobs
            .FromSqlRaw(
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
                  LIMIT 1",
                new NpgsqlParameter("deviceId", deviceId))
            .AsEnumerable()
            .FirstOrDefault();
    }

    public Job InsertJob(Job newJob)
    {
        _context.Jobs.Add(newJob);
        _context.SaveChanges();
        return newJob;
    }

    public bool UpdateJob(int id, Job updatedJob)
    {
        var existingJob = _context.Jobs
            .SingleOrDefault(x => x.Id == id);

        if (existingJob == null)
        {
            return false;
        }

        existingJob.DeviceId = updatedJob.DeviceId;
        existingJob.WorkflowId = updatedJob.WorkflowId;
        existingJob.StepNumber = updatedJob.StepNumber;
        existingJob.CommandCatalogueId = updatedJob.CommandCatalogueId;
        existingJob.PayloadJson = updatedJob.PayloadJson;
        existingJob.ProviderType = updatedJob.ProviderType;
        existingJob.Status = updatedJob.Status;
        existingJob.RequestedByAppUserId = updatedJob.RequestedByAppUserId;
        existingJob.ClaimedByDeviceCredentialId = updatedJob.ClaimedByDeviceCredentialId;
        existingJob.ClaimedAtUtc = updatedJob.ClaimedAtUtc;
        existingJob.LeaseExpiresAtUtc = updatedJob.LeaseExpiresAtUtc;
        existingJob.IsRollback = updatedJob.IsRollback;
        existingJob.RollbackOfJobHistoryId = updatedJob.RollbackOfJobHistoryId;
        existingJob.ModifiedDate = updatedJob.ModifiedDate;

        _context.SaveChanges();
        return true;
    }

    public bool DeleteJob(int id)
    {
        var existingJob = _context.Jobs
            .SingleOrDefault(x => x.Id == id);

        if (existingJob == null)
        {
            return false;
        }

        _context.Jobs.Remove(existingJob);
        _context.SaveChanges();
        return true;
    }

    public bool DeviceExistsAndActive(int deviceId)
    {
        return Exists(
            "SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @deviceId AND isactive = true)",
            new[] { new NpgsqlParameter("deviceId", deviceId) });
    }

    public int? GetDeviceMapId(int deviceId)
    {
        return NullableInt(
            "SELECT mapid FROM public.device WHERE id = @deviceId",
            new[] { new NpgsqlParameter("deviceId", deviceId) });
    }

    public bool CommandCatalogueExistsAndActive(int commandCatalogueId)
    {
        return Exists(
            "SELECT EXISTS (SELECT 1 FROM public.commandcatalogue WHERE id = @commandCatalogueId AND isactive = true)",
            new[] { new NpgsqlParameter("commandCatalogueId", commandCatalogueId) });
    }

    public string? GetCommandCatalogueNameById(int commandCatalogueId)
    {
        return NullableString(
            "SELECT name FROM public.commandcatalogue WHERE id = @commandCatalogueId",
            new[] { new NpgsqlParameter("commandCatalogueId", commandCatalogueId) });
    }

    public CommandCatalogueSnapshot? GetCommandCatalogueById(int commandCatalogueId)
    {
        const string sql = @"SELECT id, name, executionkind, rollbackkind, inversecommandname, requiresduration, isactive
                             FROM public.commandcatalogue
                             WHERE id = @commandCatalogueId";

        return QueryCommandCatalogue(sql, new[]
        {
            new NpgsqlParameter("commandCatalogueId", commandCatalogueId)
        });
    }

    public int? GetCommandCatalogueIdByName(string commandName)
    {
        return NullableInt(
            "SELECT id FROM public.commandcatalogue WHERE lower(name) = lower(@commandName) AND isactive = true",
            new[] { new NpgsqlParameter("commandName", commandName) });
    }

    public DeviceCapabilitySnapshot? GetActiveDeviceCapability(int deviceId, int commandCatalogueId)
    {
        const string sql = @"SELECT id, deviceid, commandcatalogueid, requiresmap, isactive
                             FROM public.devicecapability
                             WHERE deviceid = @deviceId AND commandcatalogueid = @commandCatalogueId AND isactive = true";

        return QueryDeviceCapability(sql, new[]
        {
            new NpgsqlParameter("deviceId", deviceId),
            new NpgsqlParameter("commandCatalogueId", commandCatalogueId)
        });
    }

    public bool IsDeviceGridPoseTrustedAndAligned(int deviceId)
    {
        return Exists(
            "SELECT EXISTS (SELECT 1 FROM public.devicestatus WHERE deviceid = @deviceId AND isgridposetrusted = true AND isgridaligned = true)",
            new[] { new NpgsqlParameter("deviceId", deviceId) });
    }

    public bool DeviceCredentialOwnsDevice(int deviceCredentialId, int deviceId)
    {
        return Exists(
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
              )",
            new[]
            {
                new NpgsqlParameter("deviceCredentialId", deviceCredentialId),
                new NpgsqlParameter("deviceId", deviceId)
            });
    }

    // SIT315 guarded dispatch transitions are implemented for the ADO provider only.
    private const string AdoOnly = "Guarded dispatch transitions are only implemented for the ADO persistence provider.";
    public List<Job> GetStaleJobsByDeviceId(int deviceId, DateTime now) => throw new NotSupportedException(AdoOnly);
    public Job? TryClaimJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime leaseExpiresAtUtc) => throw new NotSupportedException(AdoOnly);
    public bool TryUpdateQueuedJobStatus(int jobId, string newStatus, DateTime modifiedDate) => throw new NotSupportedException(AdoOnly);
    public Job? TryMarkClaimedJobExecuting(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime modifiedDate) => throw new NotSupportedException(AdoOnly);
    public Job? TryFinishClaimedJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, string[] fromStatuses, string newStatus, DateTime modifiedDate) => throw new NotSupportedException(AdoOnly);
    public bool TryRequeueStaleClaimedJob(int jobId, DateTime? claimedAtUtc, DateTime now) => throw new NotSupportedException(AdoOnly);
    public bool TryExpireStaleExecutingJob(int jobId, DateTime? claimedAtUtc, DateTime now) => throw new NotSupportedException(AdoOnly);

    private bool Exists(string sql, NpgsqlParameter[] parameters)
    {
        using var connection = new NpgsqlConnection(_context.Database.GetConnectionString());
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        connection.Open();
        return (bool)(command.ExecuteScalar() ?? false);
    }

    private int? NullableInt(string sql, NpgsqlParameter[] parameters)
    {
        using var connection = new NpgsqlConnection(_context.Database.GetConnectionString());
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        connection.Open();
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
    }

    private string? NullableString(string sql, NpgsqlParameter[] parameters)
    {
        using var connection = new NpgsqlConnection(_context.Database.GetConnectionString());
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        connection.Open();
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : result.ToString();
    }

    private DeviceCapabilitySnapshot? QueryDeviceCapability(string sql, NpgsqlParameter[] parameters)
    {
        using var connection = new NpgsqlConnection(_context.Database.GetConnectionString());
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        connection.Open();
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new DeviceCapabilitySnapshot
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            DeviceId = reader.GetInt32(reader.GetOrdinal("deviceid")),
            CommandCatalogueId = reader.GetInt32(reader.GetOrdinal("commandcatalogueid")),
            RequiresMap = reader.GetBoolean(reader.GetOrdinal("requiresmap")),
            IsActive = reader.GetBoolean(reader.GetOrdinal("isactive"))
        };
    }


    private CommandCatalogueSnapshot? QueryCommandCatalogue(string sql, NpgsqlParameter[] parameters)
    {
        using var connection = new NpgsqlConnection(_context.Database.GetConnectionString());
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        connection.Open();
        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new CommandCatalogueSnapshot
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            ExecutionKind = reader.GetString(reader.GetOrdinal("executionkind")),
            RollbackKind = reader.GetString(reader.GetOrdinal("rollbackkind")),
            InverseCommandName = reader.IsDBNull(reader.GetOrdinal("inversecommandname")) ? null : reader.GetString(reader.GetOrdinal("inversecommandname")),
            RequiresDuration = reader.GetBoolean(reader.GetOrdinal("requiresduration")),
            IsActive = reader.GetBoolean(reader.GetOrdinal("isactive"))
        };
    }

}
