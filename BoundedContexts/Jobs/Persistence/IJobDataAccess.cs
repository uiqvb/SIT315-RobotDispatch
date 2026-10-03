using RobotControllerApi.BoundedContexts.Jobs.Models;

namespace RobotControllerApi.BoundedContexts.Jobs.Persistence;

public interface IJobDataAccess
{
    List<Job> GetJobs();
    Job? GetJobById(int id);
    List<Job> GetJobsByDeviceId(int deviceId);
    List<Job> GetJobsByWorkflowId(int workflowId);
    Job? GetOldestQueuedStandaloneJobByDeviceId(int deviceId);
    Job? GetOldestQueuedJobByDeviceId(int deviceId);
    Job InsertJob(Job newJob);
    bool UpdateJob(int id, Job updatedJob);
    bool DeleteJob(int id);
    bool DeviceExistsAndActive(int deviceId);
    int? GetDeviceMapId(int deviceId);
    bool CommandCatalogueExistsAndActive(int commandCatalogueId);
    string? GetCommandCatalogueNameById(int commandCatalogueId);
    CommandCatalogueSnapshot? GetCommandCatalogueById(int commandCatalogueId);
    int? GetCommandCatalogueIdByName(string commandName);
    DeviceCapabilitySnapshot? GetActiveDeviceCapability(int deviceId, int commandCatalogueId);
    bool IsDeviceGridPoseTrustedAndAligned(int deviceId);
    bool DeviceCredentialOwnsDevice(int deviceCredentialId, int deviceId);

    // Guarded transitions: each only changes the row if it is still in the expected state, and returns null/false otherwise.
    List<Job> GetStaleJobsByDeviceId(int deviceId, DateTime now);
    List<Job> GetStaleJobs(DateTime now);
    Job? TryClaimJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime leaseExpiresAtUtc);
    bool TryUpdateQueuedJobStatus(int jobId, string newStatus, DateTime modifiedDate);
    Job? TryMarkClaimedJobExecuting(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime modifiedDate);
    Job? TryFinishClaimedJob(int jobId, int deviceCredentialId, DateTime claimedAtUtc, string[] fromStatuses, string newStatus, DateTime modifiedDate);
    bool TryRequeueStaleClaimedJob(int jobId, DateTime? claimedAtUtc, DateTime now);
    bool TryExpireStaleExecutingJob(int jobId, DateTime? claimedAtUtc, DateTime now);
}
