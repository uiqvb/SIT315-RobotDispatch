using Npgsql;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Models;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class WorkflowHistoryRepository : IWorkflowHistoryDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public WorkflowHistoryRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<WorkflowHistory> GetWorkflowHistories()
    {
        return _repo.ExecuteReader<WorkflowHistory>(
            ConnectionString,
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM public.workflowhistory
              ORDER BY id;");
    }

    public WorkflowHistory? GetWorkflowHistoryById(int id)
    {
        return _repo.ExecuteReader<WorkflowHistory>(
            ConnectionString,
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM public.workflowhistory
              WHERE id = @id;",
            new NpgsqlParameter[]
            {
                new("id", id)
            })
            .SingleOrDefault();
    }

    public List<WorkflowHistory> GetWorkflowHistoriesByWorkflowId(int workflowId)
    {
        return _repo.ExecuteReader<WorkflowHistory>(
            ConnectionString,
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM public.workflowhistory
              WHERE workflowid = @workflowId
              ORDER BY createddate DESC, id DESC;",
            new NpgsqlParameter[]
            {
                new("workflowId", workflowId)
            });
    }

    public List<WorkflowHistory> GetWorkflowHistoriesByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<WorkflowHistory>(
            ConnectionString,
            @"SELECT id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate
              FROM public.workflowhistory
              WHERE deviceid = @deviceId
              ORDER BY createddate DESC, id DESC;",
            new NpgsqlParameter[]
            {
                new("deviceId", deviceId)
            });
    }

    public WorkflowHistory InsertWorkflowHistory(WorkflowHistory newWorkflowHistory)
    {
        return _repo.ExecuteReader<WorkflowHistory>(
            ConnectionString,
            @"INSERT INTO public.workflowhistory
              (workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate)
              VALUES (@workflowId, @deviceId, @providerType, @status, @executed, @success, @startedAtUtc, @completedAtUtc, @failedStepNumber, @failureMessage, @rollbackOfWorkflowHistoryId, @createdDate)
              RETURNING id, workflowid, deviceid, providertype, status, executed, success, startedatutc, completedatutc, failedstepnumber, failuremessage, rollbackofworkflowhistoryid, createddate;",
            new NpgsqlParameter[]
            {
                new("workflowId", (object?)newWorkflowHistory.WorkflowId ?? DBNull.Value),
                new("deviceId", newWorkflowHistory.DeviceId),
                new("providerType", newWorkflowHistory.ProviderType),
                new("status", newWorkflowHistory.Status),
                new("executed", newWorkflowHistory.Executed),
                new("success", newWorkflowHistory.Success),
                new("startedAtUtc", (object?)newWorkflowHistory.StartedAtUtc ?? DBNull.Value),
                new("completedAtUtc", (object?)newWorkflowHistory.CompletedAtUtc ?? DBNull.Value),
                new("failedStepNumber", (object?)newWorkflowHistory.FailedStepNumber ?? DBNull.Value),
                new("failureMessage", (object?)newWorkflowHistory.FailureMessage ?? DBNull.Value),
                new("rollbackOfWorkflowHistoryId", (object?)newWorkflowHistory.RollbackOfWorkflowHistoryId ?? DBNull.Value),
                new("createdDate", newWorkflowHistory.CreatedDate)
            })
            .Single();
    }

    public bool DeviceExists(int deviceId)
    {
        return ReadBool(
            "SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @id) AS value;",
            new NpgsqlParameter[] { new("id", deviceId) });
    }

    public bool WorkflowExists(int workflowId)
    {
        return ReadBool(
            "SELECT EXISTS (SELECT 1 FROM public.workflow WHERE id = @id) AS value;",
            new NpgsqlParameter[] { new("id", workflowId) });
    }

    private bool ReadBool(string sql, NpgsqlParameter[] parameters)
    {
        return _repo.ExecuteReader<BoolResult>(ConnectionString, sql, parameters).FirstOrDefault()?.Value ?? false;
    }

    private class BoolResult
    {
        public bool Value { get; set; }
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<List<WorkflowHistory>> GetWorkflowHistoriesByWorkflowIdAsync(int workflowId, CancellationToken ct = default) => Task.FromResult(GetWorkflowHistoriesByWorkflowId(workflowId));
    public Task<WorkflowHistory> InsertWorkflowHistoryAsync(WorkflowHistory newWorkflowHistory, CancellationToken ct = default) => Task.FromResult(InsertWorkflowHistory(newWorkflowHistory));
}
