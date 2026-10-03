using RobotControllerApi.BoundedContexts.DeviceStatuses.Models;

namespace RobotControllerApi.BoundedContexts.DeviceStatuses.Persistence;

public interface IDeviceStatusDataAccess
{
    List<DeviceStatus> GetDeviceStatuses();
    DeviceStatus? GetDeviceStatusById(int id);
    DeviceStatus? GetDeviceStatusByDeviceId(int deviceId);
    bool DeviceStatusExistsByDeviceId(int deviceId, int? excludeId = null);
    DeviceStatus InsertDeviceStatus(DeviceStatus newDeviceStatus);
    bool UpdateDeviceStatus(int id, DeviceStatus updatedDeviceStatus);

    // Async siblings for the dispatch path; the sync versions above stay for other callers.
    Task<DeviceStatus?> GetDeviceStatusByDeviceIdAsync(int deviceId, CancellationToken ct = default);
    Task<bool> UpdateDeviceStatusAsync(int id, DeviceStatus updatedDeviceStatus, CancellationToken ct = default);
}
