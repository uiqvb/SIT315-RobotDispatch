using RobotControllerApi.BoundedContexts.DeviceStatuses.Models;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class DeviceStatusEF : IDeviceStatusDataAccess
{
    private readonly RobotContext _context;

    public DeviceStatusEF(RobotContext context)
    {
        _context = context;
    }

    public List<DeviceStatus> GetDeviceStatuses()
    {
        return _context.DeviceStatuses
            .OrderBy(x => x.Id)
            .ToList();
    }

    public DeviceStatus? GetDeviceStatusById(int id)
    {
        return _context.DeviceStatuses
            .SingleOrDefault(x => x.Id == id);
    }

    public DeviceStatus? GetDeviceStatusByDeviceId(int deviceId)
    {
        return _context.DeviceStatuses
            .OrderBy(x => x.Id)
            .FirstOrDefault(x => x.DeviceId == deviceId);
    }

    public bool DeviceStatusExistsByDeviceId(int deviceId, int? excludeId = null)
    {
        var query = _context.DeviceStatuses
            .Where(x => x.DeviceId == deviceId);

        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }

        return query.Any();
    }

    public DeviceStatus InsertDeviceStatus(DeviceStatus newDeviceStatus)
    {
        _context.DeviceStatuses.Add(newDeviceStatus);
        _context.SaveChanges();
        return newDeviceStatus;
    }

    public bool UpdateDeviceStatus(int id, DeviceStatus updatedDeviceStatus)
    {
        var existingStatus = _context.DeviceStatuses
            .SingleOrDefault(x => x.Id == id);

        if (existingStatus == null)
        {
            return false;
        }

        existingStatus.DeviceId = updatedDeviceStatus.DeviceId;
        existingStatus.ConnectionState = updatedDeviceStatus.ConnectionState;
        existingStatus.OperationalState = updatedDeviceStatus.OperationalState;
        existingStatus.LastSeenAtUtc = updatedDeviceStatus.LastSeenAtUtc;
        existingStatus.LastHeartbeatAtUtc = updatedDeviceStatus.LastHeartbeatAtUtc;
        existingStatus.PoseMapId = updatedDeviceStatus.PoseMapId;
        existingStatus.GridX = updatedDeviceStatus.GridX;
        existingStatus.GridY = updatedDeviceStatus.GridY;
        existingStatus.Facing = updatedDeviceStatus.Facing;
        existingStatus.IsGridAligned = updatedDeviceStatus.IsGridAligned;
        existingStatus.IsGridPoseTrusted = updatedDeviceStatus.IsGridPoseTrusted;
        existingStatus.PoseConfidence = updatedDeviceStatus.PoseConfidence;
        existingStatus.EstimatedXcm = updatedDeviceStatus.EstimatedXcm;
        existingStatus.EstimatedYcm = updatedDeviceStatus.EstimatedYcm;
        existingStatus.EstimatedHeadingDegrees = updatedDeviceStatus.EstimatedHeadingDegrees;
        existingStatus.IsInsideMap = updatedDeviceStatus.IsInsideMap;
        existingStatus.StatusMessage = updatedDeviceStatus.StatusMessage;
        existingStatus.LastErrorCode = updatedDeviceStatus.LastErrorCode;
        existingStatus.LastErrorMessage = updatedDeviceStatus.LastErrorMessage;
        existingStatus.ModifiedDate = updatedDeviceStatus.ModifiedDate;

        _context.SaveChanges();
        return true;
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<DeviceStatus?> GetDeviceStatusByDeviceIdAsync(int deviceId, CancellationToken ct = default) => Task.FromResult(GetDeviceStatusByDeviceId(deviceId));
    public Task<bool> UpdateDeviceStatusAsync(int id, DeviceStatus updatedDeviceStatus, CancellationToken ct = default) => Task.FromResult(UpdateDeviceStatus(id, updatedDeviceStatus));
}
