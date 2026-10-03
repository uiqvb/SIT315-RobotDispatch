using RobotControllerApi.BoundedContexts.Devices.Models;

namespace RobotControllerApi.BoundedContexts.Devices.Persistence;

public interface IDeviceDataAccess
{
    List<Device> GetDevices();
    Device? GetDeviceById(int id);
    bool DeviceExistsByIdentifier(string deviceIdentifier, int? excludeId = null);
    Device InsertDevice(Device newDevice);
    bool UpdateDevice(int id, Device updatedDevice);
    bool DeleteDevice(int id);

    // Async siblings for the dispatch path; the sync versions above stay for other callers.
    Task<Device?> GetDeviceByIdAsync(int id, CancellationToken ct = default);
}
