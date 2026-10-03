using Npgsql;
using RobotControllerApi.BoundedContexts.Workflows.Models;
using RobotControllerApi.BoundedContexts.Workflows.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class WorkflowRepository : IWorkflowDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public WorkflowRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<Workflow> GetWorkflows()
    {
        return _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              ORDER BY id;");
    }

    public Workflow? GetWorkflowById(int id)
    {
        return _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              WHERE id = @Id;",
            new NpgsqlParameter[]
            {
                new("@Id", id)
            })
            .SingleOrDefault();
    }

    public List<Workflow> GetWorkflowsByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              WHERE deviceid = @DeviceId
              ORDER BY id;",
            new NpgsqlParameter[]
            {
                new("@DeviceId", deviceId)
            });
    }

    public Workflow? GetOldestQueuedWorkflowByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              WHERE deviceid = @DeviceId AND status = 'Queued'
              ORDER BY createddate, id
              LIMIT 1;",
            new NpgsqlParameter[]
            {
                new("@DeviceId", deviceId)
            })
            .FirstOrDefault();
    }

    public Workflow InsertWorkflow(Workflow newWorkflow)
    {
        return _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"INSERT INTO public.workflow
              (deviceid, name, description, schemaversion, executionmode, providertype, status,
               requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
               isrollback, rollbackofworkflowhistoryid, createddate, modifieddate)
              VALUES (@DeviceId, @Name, @Description, @SchemaVersion, @ExecutionMode, @ProviderType, @Status,
                      @RequestedByAppUserId, @ClaimedByDeviceCredentialId, @ClaimedAtUtc, @LeaseExpiresAtUtc,
                      @IsRollback, @RollbackOfWorkflowHistoryId, @CreatedDate, @ModifiedDate)
              RETURNING id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                        requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                        isrollback, rollbackofworkflowhistoryid, createddate, modifieddate;",
            BuildParameters(newWorkflow))
            .Single();
    }

    public bool UpdateWorkflow(int id, Workflow updatedWorkflow)
    {
        var parameters = BuildParameters(updatedWorkflow).ToList();
        parameters.Add(new NpgsqlParameter("@Id", id));

        var result = _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"UPDATE public.workflow
              SET deviceid = @DeviceId,
                  name = @Name,
                  description = @Description,
                  schemaversion = @SchemaVersion,
                  executionmode = @ExecutionMode,
                  providertype = @ProviderType,
                  status = @Status,
                  requestedbyappuserid = @RequestedByAppUserId,
                  claimedbydevicecredentialid = @ClaimedByDeviceCredentialId,
                  claimedatutc = @ClaimedAtUtc,
                  leaseexpiresatutc = @LeaseExpiresAtUtc,
                  isrollback = @IsRollback,
                  rollbackofworkflowhistoryid = @RollbackOfWorkflowHistoryId,
                  modifieddate = @ModifiedDate
              WHERE id = @Id
              RETURNING id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                        requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                        isrollback, rollbackofworkflowhistoryid, createddate, modifieddate;",
            parameters.ToArray());

        return result.Any();
    }

    public bool DeleteWorkflow(int id)
    {
        var result = _repo.ExecuteReader<Workflow>(
            ConnectionString,
            @"DELETE FROM public.workflow
              WHERE id = @Id
              RETURNING id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                        requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                        isrollback, rollbackofworkflowhistoryid, createddate, modifieddate;",
            new NpgsqlParameter[]
            {
                new("@Id", id)
            });

        return result.Any();
    }

    public bool DeviceExistsAndActive(int deviceId)
    {
        return ReadBool(
            "SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @Id AND isactive = true) AS value;",
            new NpgsqlParameter[]
            {
                new("@Id", deviceId)
            });
    }

    public bool DeviceCredentialOwnsDevice(int deviceCredentialId, int deviceId)
    {
        return ReadBool(
            @"SELECT EXISTS (
                SELECT 1
                FROM public.devicecredential dc
                JOIN public.device d ON d.id = dc.deviceid
                WHERE dc.id = @CredentialId
                  AND dc.deviceid = @DeviceId
                  AND dc.isactive = true
                  AND dc.revokedatutc IS NULL
                  AND (dc.expiresatutc IS NULL OR dc.expiresatutc > (now() AT TIME ZONE 'utc'))
                  AND d.isactive = true
            ) AS value;",
            new NpgsqlParameter[]
            {
                new("@CredentialId", deviceCredentialId),
                new("@DeviceId", deviceId)
            });
    }

    private bool ReadBool(string sql, NpgsqlParameter[] parameters)
    {
        return _repo.ExecuteReader<BoolResult>(ConnectionString, sql, parameters).FirstOrDefault()?.Value ?? false;
    }

    private static NpgsqlParameter[] BuildParameters(Workflow workflow)
    {
        return new NpgsqlParameter[]
        {
            new("@DeviceId", workflow.DeviceId),
            new("@Name", workflow.Name),
            new("@Description", (object?)workflow.Description ?? DBNull.Value),
            new("@SchemaVersion", workflow.SchemaVersion),
            new("@ExecutionMode", workflow.ExecutionMode),
            new("@ProviderType", workflow.ProviderType),
            new("@Status", workflow.Status),
            new("@RequestedByAppUserId", (object?)workflow.RequestedByAppUserId ?? DBNull.Value),
            new("@ClaimedByDeviceCredentialId", (object?)workflow.ClaimedByDeviceCredentialId ?? DBNull.Value),
            new("@ClaimedAtUtc", (object?)workflow.ClaimedAtUtc ?? DBNull.Value),
            new("@LeaseExpiresAtUtc", (object?)workflow.LeaseExpiresAtUtc ?? DBNull.Value),
            new("@IsRollback", workflow.IsRollback),
            new("@RollbackOfWorkflowHistoryId", (object?)workflow.RollbackOfWorkflowHistoryId ?? DBNull.Value),
            new("@CreatedDate", workflow.CreatedDate),
            new("@ModifiedDate", workflow.ModifiedDate)
        };
    }

    private class BoolResult
    {
        public bool Value { get; set; }
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Workflow?> GetWorkflowByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetWorkflowById(id));
    public Task<bool> UpdateWorkflowAsync(int id, Workflow updatedWorkflow, CancellationToken ct = default) => Task.FromResult(UpdateWorkflow(id, updatedWorkflow));
}
