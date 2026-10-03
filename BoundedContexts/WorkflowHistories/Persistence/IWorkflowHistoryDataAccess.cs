using RobotControllerApi.BoundedContexts.WorkflowHistories.Models;

namespace RobotControllerApi.BoundedContexts.WorkflowHistories.Persistence;

public interface IWorkflowHistoryDataAccess
{
    List<WorkflowHistory> GetWorkflowHistories();
    WorkflowHistory? GetWorkflowHistoryById(int id);
    List<WorkflowHistory> GetWorkflowHistoriesByWorkflowId(int workflowId);
    List<WorkflowHistory> GetWorkflowHistoriesByDeviceId(int deviceId);
    WorkflowHistory InsertWorkflowHistory(WorkflowHistory newWorkflowHistory);
    bool DeviceExists(int deviceId);
    bool WorkflowExists(int workflowId);

    // Async siblings for the dispatch path; the sync versions above stay for other callers.
    Task<List<WorkflowHistory>> GetWorkflowHistoriesByWorkflowIdAsync(int workflowId, CancellationToken ct = default);
    Task<WorkflowHistory> InsertWorkflowHistoryAsync(WorkflowHistory newWorkflowHistory, CancellationToken ct = default);
}
