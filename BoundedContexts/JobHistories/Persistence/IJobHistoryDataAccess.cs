using RobotControllerApi.BoundedContexts.JobHistories.Models;

namespace RobotControllerApi.BoundedContexts.JobHistories.Persistence;

public interface IJobHistoryDataAccess
{
    List<JobHistory> GetJobHistories();
    JobHistory? GetJobHistoryById(int id);
    List<JobHistory> GetJobHistoriesByJobId(int jobId);
    List<JobHistory> GetJobHistoriesByWorkflowId(int workflowId);
    List<JobHistory> GetJobHistoriesByDeviceId(int deviceId);
    JobHistory InsertJobHistory(JobHistory newJobHistory);
    bool DeviceExists(int deviceId);
    bool JobExists(int jobId);
    bool WorkflowExists(int workflowId);
    bool CommandCatalogueExists(int commandCatalogueId);

    // Async siblings for the dispatch path; the sync versions above stay for other callers.
    Task<JobHistory?> GetJobHistoryByIdAsync(int id, CancellationToken ct = default);
    Task<JobHistory> InsertJobHistoryAsync(JobHistory newJobHistory, CancellationToken ct = default);
}
