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
    bool CancelJob(int id);
    bool DeactivateJob(int id);
    bool MarkJobStarted(int id, StartJobRequest request, int deviceCredentialId, int deviceId);
    bool MarkJobCompleted(int id, CompleteJobRequest request, int deviceCredentialId, int deviceId);
    bool MarkJobFailed(int id, FailJobRequest request, int deviceCredentialId, int deviceId);
}
