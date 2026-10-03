using Npgsql;
using RobotControllerApi.BoundedContexts.JobHistories.Models;
using RobotControllerApi.BoundedContexts.JobHistories.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class JobHistoryEF : EfDataAccessBase, IJobHistoryDataAccess
{
    public JobHistoryEF(RobotContext context) : base(context) { }

    public List<JobHistory> GetJobHistories() => Context.JobHistories.OrderBy(x => x.Id).ToList();

    public JobHistory? GetJobHistoryById(int id) => Context.JobHistories.Find(id);

    public List<JobHistory> GetJobHistoriesByJobId(int jobId) => Context.JobHistories
        .Where(x => x.JobId == jobId)
        .OrderBy(x => x.CreatedDate)
        .ThenBy(x => x.Id)
        .ToList();

    public List<JobHistory> GetJobHistoriesByWorkflowId(int workflowId) => Context.JobHistories
        .Where(x => x.WorkflowId == workflowId)
        .OrderBy(x => x.StepNumber)
        .ThenBy(x => x.CreatedDate)
        .ThenBy(x => x.Id)
        .ToList();

    public List<JobHistory> GetJobHistoriesByDeviceId(int deviceId) => Context.JobHistories
        .Where(x => x.DeviceId == deviceId)
        .OrderByDescending(x => x.CreatedDate)
        .ThenByDescending(x => x.Id)
        .ToList();

    public JobHistory InsertJobHistory(JobHistory newJobHistory)
    {
        Context.JobHistories.Add(newJobHistory);
        Context.SaveChanges();
        return newJobHistory;
    }

    public bool DeviceExists(int deviceId) => Exists("SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @id)", new[] { new NpgsqlParameter("@id", deviceId) });

    public bool JobExists(int jobId) => Exists("SELECT EXISTS (SELECT 1 FROM public.job WHERE id = @id)", new[] { new NpgsqlParameter("@id", jobId) });

    public bool WorkflowExists(int workflowId) => Exists("SELECT EXISTS (SELECT 1 FROM public.workflow WHERE id = @id)", new[] { new NpgsqlParameter("@id", workflowId) });

    public bool CommandCatalogueExists(int commandCatalogueId) => Exists("SELECT EXISTS (SELECT 1 FROM public.commandcatalogue WHERE id = @id)", new[] { new NpgsqlParameter("@id", commandCatalogueId) });

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<JobHistory?> GetJobHistoryByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetJobHistoryById(id));
    public Task<JobHistory> InsertJobHistoryAsync(JobHistory newJobHistory, CancellationToken ct = default) => Task.FromResult(InsertJobHistory(newJobHistory));
}
