using Npgsql;
using RobotControllerApi.BoundedContexts.JobHistories.Models;
using RobotControllerApi.BoundedContexts.JobHistories.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class JobHistoryRepository : IJobHistoryDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public JobHistoryRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    private const string SelectColumns = @"id, jobid, workflowid, stepnumber, deviceid, commandcatalogueid, commandname, payloadjson, providertype, executionkind, rollbackkind, executed, success, resultjson, failurecode, failuremessage, startedatutc, completedatutc, durationms, rollbackofjobhistoryid, createddate";

    public List<JobHistory> GetJobHistories()
    {
        return _repo.ExecuteReader<JobHistory>(
            ConnectionString,
            $@"SELECT {SelectColumns}
              FROM public.jobhistory
              ORDER BY id;");
    }

    public JobHistory? GetJobHistoryById(int id)
    {
        return _repo.ExecuteReader<JobHistory>(
            ConnectionString,
            $@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE id = @id;",
            new NpgsqlParameter[]
            {
                new("id", id)
            })
            .SingleOrDefault();
    }

    public List<JobHistory> GetJobHistoriesByJobId(int jobId)
    {
        return _repo.ExecuteReader<JobHistory>(
            ConnectionString,
            $@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE jobid = @jobId
              ORDER BY createddate, id;",
            new NpgsqlParameter[]
            {
                new("jobId", jobId)
            });
    }

    public List<JobHistory> GetJobHistoriesByWorkflowId(int workflowId)
    {
        return _repo.ExecuteReader<JobHistory>(
            ConnectionString,
            $@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE workflowid = @workflowId
              ORDER BY stepnumber, createddate, id;",
            new NpgsqlParameter[]
            {
                new("workflowId", workflowId)
            });
    }

    public List<JobHistory> GetJobHistoriesByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<JobHistory>(
            ConnectionString,
            $@"SELECT {SelectColumns}
              FROM public.jobhistory
              WHERE deviceid = @deviceId
              ORDER BY createddate DESC, id DESC;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            });
    }

    public JobHistory InsertJobHistory(JobHistory newJobHistory)
    {
        return _repo.ExecuteReader<JobHistory>(
            ConnectionString,
            $@"INSERT INTO public.jobhistory
              (jobid, workflowid, stepnumber, deviceid, commandcatalogueid, commandname, payloadjson, providertype, executionkind, rollbackkind, executed, success, resultjson, failurecode, failuremessage, startedatutc, completedatutc, durationms, rollbackofjobhistoryid, createddate)
              VALUES (@jobId, @workflowId, @stepNumber, @deviceId, @commandCatalogueId, @commandName, @payloadJson::jsonb, @providerType, @executionKind, @rollbackKind, @executed, @success, @resultJson::jsonb, @failureCode, @failureMessage, @startedAtUtc, @completedAtUtc, @durationMs, @rollbackOfJobHistoryId, @createdDate)
              RETURNING {SelectColumns};",
            new NpgsqlParameter[]
            {
                new("jobId", (object?)newJobHistory.JobId ?? DBNull.Value),
                new("workflowId", (object?)newJobHistory.WorkflowId ?? DBNull.Value),
                new("stepNumber", (object?)newJobHistory.StepNumber ?? DBNull.Value),
                new("deviceId", newJobHistory.DeviceId),
                new("commandCatalogueId", (object?)newJobHistory.CommandCatalogueId ?? DBNull.Value),
                new("commandName", newJobHistory.CommandName),
                new("payloadJson", newJobHistory.PayloadJson),
                new("providerType", newJobHistory.ProviderType),
                new("executionKind", newJobHistory.ExecutionKind),
                new("rollbackKind", newJobHistory.RollbackKind),
                new("executed", newJobHistory.Executed),
                new("success", newJobHistory.Success),
                new("resultJson", (object?)newJobHistory.ResultJson ?? DBNull.Value),
                new("failureCode", (object?)newJobHistory.FailureCode ?? DBNull.Value),
                new("failureMessage", (object?)newJobHistory.FailureMessage ?? DBNull.Value),
                new("startedAtUtc", (object?)newJobHistory.StartedAtUtc ?? DBNull.Value),
                new("completedAtUtc", (object?)newJobHistory.CompletedAtUtc ?? DBNull.Value),
                new("durationMs", (object?)newJobHistory.DurationMs ?? DBNull.Value),
                new("rollbackOfJobHistoryId", (object?)newJobHistory.RollbackOfJobHistoryId ?? DBNull.Value),
                new("createdDate", newJobHistory.CreatedDate)
            })
            .Single();
    }

    public bool DeviceExists(int deviceId) => ReadBool(
        "SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @id) AS value;",
        new NpgsqlParameter[] { new("id", deviceId) });

    public bool JobExists(int jobId) => ReadBool(
        "SELECT EXISTS (SELECT 1 FROM public.job WHERE id = @id) AS value;",
        new NpgsqlParameter[] { new("id", jobId) });

    public bool WorkflowExists(int workflowId) => ReadBool(
        "SELECT EXISTS (SELECT 1 FROM public.workflow WHERE id = @id) AS value;",
        new NpgsqlParameter[] { new("id", workflowId) });

    public bool CommandCatalogueExists(int commandCatalogueId) => ReadBool(
        "SELECT EXISTS (SELECT 1 FROM public.commandcatalogue WHERE id = @id) AS value;",
        new NpgsqlParameter[] { new("id", commandCatalogueId) });

    private bool ReadBool(string sql, NpgsqlParameter[] parameters)
    {
        return _repo.ExecuteReader<BoolResult>(ConnectionString, sql, parameters).FirstOrDefault()?.Value ?? false;
    }

    private class BoolResult
    {
        public bool Value { get; set; }
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<JobHistory?> GetJobHistoryByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetJobHistoryById(id));
    public Task<JobHistory> InsertJobHistoryAsync(JobHistory newJobHistory, CancellationToken ct = default) => Task.FromResult(InsertJobHistory(newJobHistory));
}
