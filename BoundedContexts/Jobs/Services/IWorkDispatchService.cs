using RobotControllerApi.BoundedContexts.Jobs.Dtos;

namespace RobotControllerApi.BoundedContexts.Jobs.Services;

public interface IWorkDispatchService
{
    Task<WorkItemClaimResponse?> ClaimNextWorkItemAsync(int deviceId, ClaimJobRequest request, int deviceCredentialId, int authenticatedDeviceId, CancellationToken ct = default);

    // Replays a robot's offline rollback so the backend's pose matches physical reality again.
    Task<OfflineRollbackReconcileResponse> ReportOfflineRollbackAsync(int deviceId, ReportOfflineRollbackRequest request, int deviceCredentialId, int authenticatedDeviceId, CancellationToken ct = default);

    // Drains leases that expired while nobody was polling. Returns the number of jobs settled.
    Task<int> ExpireStaleWorkForAllDevicesAsync(CancellationToken ct = default);
}
