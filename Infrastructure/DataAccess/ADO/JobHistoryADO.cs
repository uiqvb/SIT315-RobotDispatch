using Npgsql;
using RobotControllerApi.BoundedContexts.JobHistories.Models;
using RobotControllerApi.BoundedContexts.JobHistories.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class JobHistoryADO : IJobHistoryDataAccess
{
    private readonly DbConfig _dbConfig;

    public JobHistoryADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private const string SelectColumns = @"id, jobid, workflowid, stepnumber, deviceid, commandcatalogueid, commandname, payloadjson, providertype, executionkind, rollbackkind, executed, success, resultjson, failurecode, failuremessage, startedatutc, completedatutc, durationms, rollbackofjobhistoryid, createddate";

    public List<JobHistory> GetJobHistories()
    {
        var results = new List<JobHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand($@"SELECT {SelectColumns}
              FROM public.jobhistory
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJobHistory(dr));
        }

        return results;
    }

    private const string GetJobHistoryByIdSql =
        $@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE id = @id;";

    public JobHistory? GetJobHistoryById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(GetJobHistoryByIdSql, conn);

        cmd.Parameters.AddWithValue("id", id);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapJobHistory(dr) : null;
    }

    public Task<JobHistory?> GetJobHistoryByIdAsync(int id, CancellationToken ct = default) =>
        AdoAsync.QuerySingleAsync(_dbConfig, GetJobHistoryByIdSql, cmd => cmd.Parameters.AddWithValue("id", id), MapJobHistory, ct);

    public List<JobHistory> GetJobHistoriesByJobId(int jobId)
    {
        var results = new List<JobHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand($@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE jobid = @jobId
              ORDER BY createddate, id;", conn);

        cmd.Parameters.AddWithValue("jobId", jobId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJobHistory(dr));
        }

        return results;
    }

    public List<JobHistory> GetJobHistoriesByWorkflowId(int workflowId)
    {
        var results = new List<JobHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand($@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE workflowid = @workflowId
              ORDER BY stepnumber, createddate, id;", conn);

        cmd.Parameters.AddWithValue("workflowId", workflowId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJobHistory(dr));
        }

        return results;
    }

    public List<JobHistory> GetJobHistoriesByDeviceId(int deviceId)
    {
        var results = new List<JobHistory>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand($@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE deviceid = @deviceId
              ORDER BY createddate DESC, id DESC;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapJobHistory(dr));
        }

        return results;
    }

    private const string InsertJobHistorySql =
        $@"INSERT INTO public.jobhistory
              (jobid, workflowid, stepnumber, deviceid, commandcatalogueid, commandname, payloadjson, providertype, executionkind, rollbackkind, executed, success, resultjson, failurecode, failuremessage, startedatutc, completedatutc, durationms, rollbackofjobhistoryid, createddate)
              VALUES (@jobId, @workflowId, @stepNumber, @deviceId, @commandCatalogueId, @commandName, @payloadJson::jsonb, @providerType, @executionKind, @rollbackKind, @executed, @success, @resultJson::jsonb, @failureCode, @failureMessage, @startedAtUtc, @completedAtUtc, @durationMs, @rollbackOfJobHistoryId, @createdDate)
              RETURNING {SelectColumns};";

    public JobHistory InsertJobHistory(JobHistory newJobHistory)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(InsertJobHistorySql, conn);

        AddParameters(cmd, newJobHistory);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapJobHistory(dr);
        }

        throw new InvalidOperationException("InsertJobHistory failed to return the inserted row.");
    }

    public async Task<JobHistory> InsertJobHistoryAsync(JobHistory newJobHistory, CancellationToken ct = default) =>
        await AdoAsync.QuerySingleAsync(_dbConfig, InsertJobHistorySql, cmd => AddParameters(cmd, newJobHistory), MapJobHistory, ct)
        ?? throw new InvalidOperationException("InsertJobHistory failed to return the inserted row.");

    public bool DeviceExists(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(@"SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @id);", conn);
        cmd.Parameters.AddWithValue("id", deviceId);
        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    public bool JobExists(int jobId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(@"SELECT EXISTS (SELECT 1 FROM public.job WHERE id = @id);", conn);
        cmd.Parameters.AddWithValue("id", jobId);
        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    public bool WorkflowExists(int workflowId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(@"SELECT EXISTS (SELECT 1 FROM public.workflow WHERE id = @id);", conn);
        cmd.Parameters.AddWithValue("id", workflowId);
        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    public bool CommandCatalogueExists(int commandCatalogueId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(@"SELECT EXISTS (SELECT 1 FROM public.commandcatalogue WHERE id = @id);", conn);
        cmd.Parameters.AddWithValue("id", commandCatalogueId);
        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    private static void AddParameters(NpgsqlCommand cmd, JobHistory jobHistory)
    {
        cmd.Parameters.AddWithValue("jobId", (object?)jobHistory.JobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("workflowId", (object?)jobHistory.WorkflowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stepNumber", (object?)jobHistory.StepNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("deviceId", jobHistory.DeviceId);
        cmd.Parameters.AddWithValue("commandCatalogueId", (object?)jobHistory.CommandCatalogueId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("commandName", jobHistory.CommandName);
        cmd.Parameters.AddWithValue("payloadJson", jobHistory.PayloadJson);
        cmd.Parameters.AddWithValue("providerType", jobHistory.ProviderType);
        cmd.Parameters.AddWithValue("executionKind", jobHistory.ExecutionKind);
        cmd.Parameters.AddWithValue("rollbackKind", jobHistory.RollbackKind);
        cmd.Parameters.AddWithValue("executed", jobHistory.Executed);
        cmd.Parameters.AddWithValue("success", jobHistory.Success);
        cmd.Parameters.AddWithValue("resultJson", (object?)jobHistory.ResultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failureCode", (object?)jobHistory.FailureCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("failureMessage", (object?)jobHistory.FailureMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("startedAtUtc", (object?)jobHistory.StartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completedAtUtc", (object?)jobHistory.CompletedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("durationMs", (object?)jobHistory.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rollbackOfJobHistoryId", (object?)jobHistory.RollbackOfJobHistoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("createdDate", jobHistory.CreatedDate);
    }

    private static JobHistory MapJobHistory(NpgsqlDataReader dr)
    {
        return new JobHistory
        {
            Id = dr.GetInt32(0),
            JobId = dr.IsDBNull(1) ? null : dr.GetInt32(1),
            WorkflowId = dr.IsDBNull(2) ? null : dr.GetInt32(2),
            StepNumber = dr.IsDBNull(3) ? null : dr.GetInt32(3),
            DeviceId = dr.GetInt32(4),
            CommandCatalogueId = dr.IsDBNull(5) ? null : dr.GetInt32(5),
            CommandName = dr.GetString(6),
            PayloadJson = dr.GetString(7),
            ProviderType = dr.GetString(8),
            ExecutionKind = dr.GetString(9),
            RollbackKind = dr.GetString(10),
            Executed = dr.GetBoolean(11),
            Success = dr.GetBoolean(12),
            ResultJson = dr.IsDBNull(13) ? null : dr.GetString(13),
            FailureCode = dr.IsDBNull(14) ? null : dr.GetString(14),
            FailureMessage = dr.IsDBNull(15) ? null : dr.GetString(15),
            StartedAtUtc = dr.IsDBNull(16) ? null : dr.GetDateTime(16),
            CompletedAtUtc = dr.IsDBNull(17) ? null : dr.GetDateTime(17),
            DurationMs = dr.IsDBNull(18) ? null : dr.GetInt32(18),
            RollbackOfJobHistoryId = dr.IsDBNull(19) ? null : dr.GetInt32(19),
            CreatedDate = dr.GetDateTime(20)
        };
    }
}
