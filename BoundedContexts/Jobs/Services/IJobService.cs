using RobotControllerApi.BoundedContexts.Jobs.Dtos;

namespace RobotControllerApi.BoundedContexts.Jobs.Services;

public interface IJobService
{
    List<JobResponse> GetJobs();
    JobResponse? GetJobById(int id);
    List<JobResponse> GetJobsByDeviceId(int deviceId);
    List<JobResponse> GetJobsByWorkflowId(int workflowId);
    JobResponse CreateJob(int deviceId, CreateJobRequest request, int? requestedByAppUserId = null);
    bool UpdateJob(int id, UpdateJobRequest request);
    bool DeleteJob(int id);
    Task<bool> CancelJobAsync(int id, CancellationToken ct = default);
    Task<bool> DeactivateJobAsync(int id, CancellationToken ct = default);
    Task<bool> MarkJobStartedAsync(int id, StartJobRequest request, int deviceCredentialId, int deviceId, CancellationToken ct = default);
    Task<bool> MarkJobCompletedAsync(int id, CompleteJobRequest request, int deviceCredentialId, int deviceId, CancellationToken ct = default);
    Task<bool> MarkJobFailedAsync(int id, FailJobRequest request, int deviceCredentialId, int deviceId, CancellationToken ct = default);
}
