using Npgsql;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Models;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class WorkflowHistoryADO : IWorkflowHistoryDataAccess
{
    private readonly DbConfig _dbConfig;

    public WorkflowHistoryADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<WorkflowHistory> GetWorkflowHistories()
    {
        var results = new List<WorkflowHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM workflowhistory
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapWorkflowHistory(dr));
        }

        return results;
    }

    public WorkflowHistory? GetWorkflowHistoryById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM workflowhistory
              WHERE id = @Id;", conn);

        cmd.Parameters.AddWithValue("@Id", id);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapWorkflowHistory(dr);
        }

        return null;
    }

    private const string GetWorkflowHistoriesByWorkflowIdSql =
        @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM workflowhistory
              WHERE workflowid = @WorkflowId
              ORDER BY createddate DESC, id DESC;";

    public List<WorkflowHistory> GetWorkflowHistoriesByWorkflowId(int workflowId)
    {
        var results = new List<WorkflowHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(GetWorkflowHistoriesByWorkflowIdSql, conn);

        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapWorkflowHistory(dr));
        }

        return results;
    }

    public Task<List<WorkflowHistory>> GetWorkflowHistoriesByWorkflowIdAsync(int workflowId, CancellationToken ct = default) =>
        AdoAsync.QueryListAsync(_dbConfig, GetWorkflowHistoriesByWorkflowIdSql, cmd => cmd.Parameters.AddWithValue("@WorkflowId", workflowId), MapWorkflowHistory, ct);

    public List<WorkflowHistory> GetWorkflowHistoriesByDeviceId(int deviceId)
    {
        var results = new List<WorkflowHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM workflowhistory
              WHERE deviceid = @DeviceId
              ORDER BY createddate DESC, id DESC;", conn);

        cmd.Parameters.AddWithValue("@DeviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapWorkflowHistory(dr));
        }

        return results;
    }

    private const string InsertWorkflowHistorySql =
        @"INSERT INTO workflowhistory
              (workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate)
              VALUES (@WorkflowId, @DeviceId, @ProviderType, @Status, @Executed, @Success, @StartedAtUtc, @CompletedAtUtc, @FailedStepNumber, @FailureMessage, @RollbackOfWorkflowHistoryId, @CreatedDate)
              RETURNING id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate;";

    public WorkflowHistory InsertWorkflowHistory(WorkflowHistory newWorkflowHistory)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(InsertWorkflowHistorySql, conn);

        AddInsertParameters(cmd, newWorkflowHistory);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapWorkflowHistory(dr);
        }

        throw new InvalidOperationException("WorkflowHistory insert failed.");
    }

    public async Task<WorkflowHistory> InsertWorkflowHistoryAsync(WorkflowHistory newWorkflowHistory, CancellationToken ct = default) =>
        await AdoAsync.QuerySingleAsync(_dbConfig, InsertWorkflowHistorySql, cmd => AddInsertParameters(cmd, newWorkflowHistory), MapWorkflowHistory, ct)
        ?? throw new InvalidOperationException("WorkflowHistory insert failed.");

    private static void AddInsertParameters(NpgsqlCommand cmd, WorkflowHistory newWorkflowHistory)
    {
        cmd.Parameters.AddWithValue("@WorkflowId", (object?)newWorkflowHistory.WorkflowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@DeviceId", newWorkflowHistory.DeviceId);
        cmd.Parameters.AddWithValue("@ProviderType", newWorkflowHistory.ProviderType);
        cmd.Parameters.AddWithValue("@Status", newWorkflowHistory.Status);
        cmd.Parameters.AddWithValue("@Executed", newWorkflowHistory.Executed);
        cmd.Parameters.AddWithValue("@Success", newWorkflowHistory.Success);
        cmd.Parameters.AddWithValue("@StartedAtUtc", (object?)newWorkflowHistory.StartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)newWorkflowHistory.CompletedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FailedStepNumber", (object?)newWorkflowHistory.FailedStepNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FailureMessage", (object?)newWorkflowHistory.FailureMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RollbackOfWorkflowHistoryId", (object?)newWorkflowHistory.RollbackOfWorkflowHistoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedDate", newWorkflowHistory.CreatedDate);
    }

    public bool DeviceExists(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (SELECT 1 FROM device WHERE id = @Id);", conn);

        cmd.Parameters.AddWithValue("@Id", deviceId);

        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    public bool WorkflowExists(int workflowId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT EXISTS (SELECT 1 FROM workflow WHERE id = @Id);", conn);

        cmd.Parameters.AddWithValue("@Id", workflowId);

        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    private static WorkflowHistory MapWorkflowHistory(NpgsqlDataReader dr)
    {
        return new WorkflowHistory
        {
            Id = dr.GetInt32(0),
            WorkflowId = dr.IsDBNull(1) ? null : dr.GetInt32(1),
            DeviceId = dr.GetInt32(2),
            ProviderType = dr.GetString(3),
            Status = dr.GetString(4),
            Executed = dr.GetBoolean(5),
            Success = dr.GetBoolean(6),
            StartedAtUtc = dr.IsDBNull(7) ? null : dr.GetDateTime(7),
            CompletedAtUtc = dr.IsDBNull(8) ? null : dr.GetDateTime(8),
            FailedStepNumber = dr.IsDBNull(9) ? null : dr.GetInt32(9),
            FailureMessage = dr.IsDBNull(10) ? null : dr.GetString(10),
            RollbackOfWorkflowHistoryId = dr.IsDBNull(11) ? null : dr.GetInt32(11),
            CreatedDate = dr.GetDateTime(12)
        };
    }
}
