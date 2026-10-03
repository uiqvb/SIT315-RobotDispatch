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

    // Async siblings for the dispatch path; the sync versions above stay for admin and dashboard callers.
    Task<Job?> GetJobByIdAsync(int id, CancellationToken ct = default);
    Task<List<Job>> GetJobsByWorkflowIdAsync(int workflowId, CancellationToken ct = default);
    Task<Job?> GetOldestQueuedJobByDeviceIdAsync(int deviceId, CancellationToken ct = default);
    Task<bool> UpdateJobAsync(int id, Job updatedJob, CancellationToken ct = default);
    Task<int?> GetDeviceMapIdAsync(int deviceId, CancellationToken ct = default);
    Task<string?> GetCommandCatalogueNameByIdAsync(int commandCatalogueId, CancellationToken ct = default);
    Task<CommandCatalogueSnapshot?> GetCommandCatalogueByIdAsync(int commandCatalogueId, CancellationToken ct = default);
    Task<int?> GetCommandCatalogueIdByNameAsync(string commandName, CancellationToken ct = default);
    Task<DeviceCapabilitySnapshot?> GetActiveDeviceCapabilityAsync(int deviceId, int commandCatalogueId, CancellationToken ct = default);

    // Guarded transitions: each only changes the row if it is still in the expected state, and returns null/false otherwise.
    Task<List<Job>> GetStaleJobsByDeviceIdAsync(int deviceId, DateTime now, CancellationToken ct = default);
    Task<List<Job>> GetStaleJobsAsync(DateTime now, CancellationToken ct = default);
    Task<Job?> TryClaimJobAsync(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime leaseExpiresAtUtc, CancellationToken ct = default);
    Task<bool> TryUpdateQueuedJobStatusAsync(int jobId, string newStatus, DateTime modifiedDate, CancellationToken ct = default);
    Task<Job?> TryMarkClaimedJobExecutingAsync(int jobId, int deviceCredentialId, DateTime claimedAtUtc, DateTime modifiedDate, CancellationToken ct = default);
    Task<Job?> TryFinishClaimedJobAsync(int jobId, int deviceCredentialId, DateTime claimedAtUtc, string[] fromStatuses, string newStatus, DateTime modifiedDate, CancellationToken ct = default);
    Task<bool> TryRequeueStaleClaimedJobAsync(int jobId, DateTime? claimedAtUtc, DateTime now, CancellationToken ct = default);
    Task<bool> TryExpireStaleExecutingJobAsync(int jobId, DateTime? claimedAtUtc, DateTime now, CancellationToken ct = default);
}
