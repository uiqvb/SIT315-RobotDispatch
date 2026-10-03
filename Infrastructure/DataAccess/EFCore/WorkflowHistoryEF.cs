using Npgsql;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Models;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class WorkflowHistoryEF : EfDataAccessBase, IWorkflowHistoryDataAccess
{
    public WorkflowHistoryEF(RobotContext context) : base(context) { }

    public List<WorkflowHistory> GetWorkflowHistories()
    {
        return Context.WorkflowHistories
            .OrderBy(x => x.Id)
            .ToList();
    }

    public WorkflowHistory? GetWorkflowHistoryById(int id)
    {
        return Context.WorkflowHistories
            .SingleOrDefault(x => x.Id == id);
    }

    public List<WorkflowHistory> GetWorkflowHistoriesByWorkflowId(int workflowId)
    {
        return Context.WorkflowHistories
            .Where(x => x.WorkflowId == workflowId)
            .OrderByDescending(x => x.CreatedDate)
            .ThenByDescending(x => x.Id)
            .ToList();
    }

    public List<WorkflowHistory> GetWorkflowHistoriesByDeviceId(int deviceId)
    {
        return Context.WorkflowHistories
            .Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.CreatedDate)
            .ThenByDescending(x => x.Id)
            .ToList();
    }

    public WorkflowHistory InsertWorkflowHistory(WorkflowHistory newWorkflowHistory)
    {
        Context.WorkflowHistories.Add(newWorkflowHistory);
        Context.SaveChanges();
        return newWorkflowHistory;
    }

    public bool DeviceExists(int deviceId)
    {
        return Exists(
            "SELECT EXISTS (SELECT 1 FROM device WHERE id = @Id)",
            new[] { new NpgsqlParameter("@Id", deviceId) });
    }

    public bool WorkflowExists(int workflowId)
    {
        return Exists(
            "SELECT EXISTS (SELECT 1 FROM workflow WHERE id = @Id)",
            new[] { new NpgsqlParameter("@Id", workflowId) });
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<List<WorkflowHistory>> GetWorkflowHistoriesByWorkflowIdAsync(int workflowId, CancellationToken ct = default) => Task.FromResult(GetWorkflowHistoriesByWorkflowId(workflowId));
    public Task<WorkflowHistory> InsertWorkflowHistoryAsync(WorkflowHistory newWorkflowHistory, CancellationToken ct = default) => Task.FromResult(InsertWorkflowHistory(newWorkflowHistory));
}
