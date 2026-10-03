using Npgsql;
using RobotControllerApi.BoundedContexts.Workflows.Models;
using RobotControllerApi.BoundedContexts.Workflows.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class WorkflowADO : IWorkflowDataAccess
{
    private readonly DbConfig _dbConfig;

    public WorkflowADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<Workflow> GetWorkflows()
    {
        var results = new List<Workflow>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapWorkflow(dr));
        }

        return results;
    }

    private const string GetWorkflowByIdSql =
        @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              WHERE id = @Id;";

    public Workflow? GetWorkflowById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(GetWorkflowByIdSql, conn);

        cmd.Parameters.AddWithValue("@Id", id);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapWorkflow(dr) : null;
    }

    public Task<Workflow?> GetWorkflowByIdAsync(int id, CancellationToken ct = default) =>
        AdoAsync.QuerySingleAsync(_dbConfig, GetWorkflowByIdSql, cmd => cmd.Parameters.AddWithValue("@Id", id), MapWorkflow, ct);

    public List<Workflow> GetWorkflowsByDeviceId(int deviceId)
    {
        var results = new List<Workflow>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              WHERE deviceid = @DeviceId
              ORDER BY id;", conn);

        cmd.Parameters.AddWithValue("@DeviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapWorkflow(dr));
        }

        return results;
    }

    public Workflow? GetOldestQueuedWorkflowByDeviceId(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                     requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                     isrollback, rollbackofworkflowhistoryid, createddate, modifieddate
              FROM public.workflow
              WHERE deviceid = @DeviceId AND status = 'Queued'
              ORDER BY createddate, id
              LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("@DeviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapWorkflow(dr) : null;
    }

    public Workflow InsertWorkflow(Workflow newWorkflow)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.workflow
              (deviceid, name, description, schemaversion, executionmode, providertype, status,
               requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
               isrollback, rollbackofworkflowhistoryid, createddate, modifieddate)
              VALUES (@DeviceId, @Name, @Description, @SchemaVersion, @ExecutionMode, @ProviderType, @Status,
                      @RequestedByAppUserId, @ClaimedByDeviceCredentialId, @ClaimedAtUtc, @LeaseExpiresAtUtc,
                      @IsRollback, @RollbackOfWorkflowHistoryId, @CreatedDate, @ModifiedDate)
              RETURNING id, deviceid, name, description, schemaversion, executionmode, providertype, status,
                        requestedbyappuserid, claimedbydevicecredentialid, claimedatutc, leaseexpiresatutc,
                        isrollback, rollbackofworkflowhistoryid, createddate, modifieddate;", conn);

        AddWorkflowParameters(cmd, newWorkflow);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapWorkflow(dr);
        }

        throw new InvalidOperationException("Workflow insert failed.");
    }

    private const string UpdateWorkflowSql =
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
              WHERE id = @Id;";

    public bool UpdateWorkflow(int id, Workflow updatedWorkflow)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(UpdateWorkflowSql, conn);

        cmd.Parameters.AddWithValue("@Id", id);
        AddWorkflowParameters(cmd, updatedWorkflow);

        return cmd.ExecuteNonQuery() > 0;
    }

    public async Task<bool> UpdateWorkflowAsync(int id, Workflow updatedWorkflow, CancellationToken ct = default) =>
        await AdoAsync.ExecuteAsync(_dbConfig, UpdateWorkflowSql, cmd =>
        {
            cmd.Parameters.AddWithValue("@Id", id);
            AddWorkflowParameters(cmd, updatedWorkflow);
        }, ct) > 0;

    public bool DeleteWorkflow(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"DELETE FROM public.workflow
              WHERE id = @Id;", conn);

        cmd.Parameters.AddWithValue("@Id", id);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeviceExistsAndActive(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @Id AND isactive = true);", conn);

        cmd.Parameters.AddWithValue("@Id", deviceId);

        var result = cmd.ExecuteScalar();
        return result is bool value && value;
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
                WHERE dc.id = @CredentialId
                  AND dc.deviceid = @DeviceId
                  AND dc.isactive = true
                  AND dc.revokedatutc IS NULL
                  AND (dc.expiresatutc IS NULL OR dc.expiresatutc > (now() AT TIME ZONE 'utc'))
                  AND d.isactive = true);", conn);

        cmd.Parameters.AddWithValue("@CredentialId", deviceCredentialId);
        cmd.Parameters.AddWithValue("@DeviceId", deviceId);

        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    private static void AddWorkflowParameters(NpgsqlCommand cmd, Workflow workflow)
    {
        cmd.Parameters.AddWithValue("@DeviceId", workflow.DeviceId);
        cmd.Parameters.AddWithValue("@Name", workflow.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)workflow.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SchemaVersion", workflow.SchemaVersion);
        cmd.Parameters.AddWithValue("@ExecutionMode", workflow.ExecutionMode);
        cmd.Parameters.AddWithValue("@ProviderType", workflow.ProviderType);
        cmd.Parameters.AddWithValue("@Status", workflow.Status);
        cmd.Parameters.AddWithValue("@RequestedByAppUserId", (object?)workflow.RequestedByAppUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ClaimedByDeviceCredentialId", (object?)workflow.ClaimedByDeviceCredentialId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ClaimedAtUtc", (object?)workflow.ClaimedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LeaseExpiresAtUtc", (object?)workflow.LeaseExpiresAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsRollback", workflow.IsRollback);
        cmd.Parameters.AddWithValue("@RollbackOfWorkflowHistoryId", (object?)workflow.RollbackOfWorkflowHistoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedDate", workflow.CreatedDate);
        cmd.Parameters.AddWithValue("@ModifiedDate", workflow.ModifiedDate);
    }

    private static Workflow MapWorkflow(NpgsqlDataReader dr)
    {
        return new Workflow
        {
            Id = dr.GetInt32(0),
            DeviceId = dr.GetInt32(1),
            Name = dr.GetString(2),
            Description = dr.IsDBNull(3) ? null : dr.GetString(3),
            SchemaVersion = dr.GetString(4),
            ExecutionMode = dr.GetString(5),
            ProviderType = dr.GetString(6),
            Status = dr.GetString(7),
            RequestedByAppUserId = dr.IsDBNull(8) ? null : dr.GetInt32(8),
            ClaimedByDeviceCredentialId = dr.IsDBNull(9) ? null : dr.GetInt32(9),
            ClaimedAtUtc = dr.IsDBNull(10) ? null : dr.GetDateTime(10),
            LeaseExpiresAtUtc = dr.IsDBNull(11) ? null : dr.GetDateTime(11),
            IsRollback = dr.GetBoolean(12),
            RollbackOfWorkflowHistoryId = dr.IsDBNull(13) ? null : dr.GetInt32(13),
            CreatedDate = dr.GetDateTime(14),
            ModifiedDate = dr.GetDateTime(15)
        };
    }
}
