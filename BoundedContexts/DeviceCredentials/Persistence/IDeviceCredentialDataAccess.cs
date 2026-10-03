using RobotControllerApi.BoundedContexts.DeviceCredentials.Models;

namespace RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;

public interface IDeviceCredentialDataAccess
{
    List<DeviceCredential> GetDeviceCredentials();
    DeviceCredential? GetDeviceCredentialById(int id);
    List<DeviceCredential> GetDeviceCredentialsByDeviceId(int deviceId);
    bool DeviceCredentialExistsByCredentialIdentifier(string credentialIdentifier, int? excludeId = null);
    DeviceCredential InsertDeviceCredential(DeviceCredential newDeviceCredential);
    bool UpdateDeviceCredential(int id, DeviceCredential updatedDeviceCredential);
    bool DeleteDeviceCredential(int id);
    DeviceCredential? GetDeviceCredentialByCredentialIdentifier(string credentialIdentifier);
    bool UpdateDeviceCredentialLastUsed(int id, DateTime lastUsedAtUtc, string? lastUsedIpAddress, string? lastUsedUserAgent);
}
