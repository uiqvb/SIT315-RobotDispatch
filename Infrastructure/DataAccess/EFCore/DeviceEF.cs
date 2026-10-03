using RobotControllerApi.BoundedContexts.Devices.Models;
using RobotControllerApi.BoundedContexts.Devices.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class DeviceEF : IDeviceDataAccess
{
    private readonly RobotContext _context;

    public DeviceEF(RobotContext context)
    {
        _context = context;
    }

    public List<Device> GetDevices()
    {
        return _context.Devices
            .OrderBy(x => x.Id)
            .ToList();
    }

    public Device? GetDeviceById(int id)
    {
        return _context.Devices
            .SingleOrDefault(x => x.Id == id);
    }

    public bool DeviceExistsByIdentifier(string deviceIdentifier, int? excludeId = null)
    {
        var query = _context.Devices
            .Where(x => x.DeviceIdentifier.ToUpper() == deviceIdentifier.ToUpper());

        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }

        return query.Any();
    }

    public Device InsertDevice(Device newDevice)
    {
        _context.Devices.Add(newDevice);
        _context.SaveChanges();
        return newDevice;
    }

    public bool UpdateDevice(int id, Device updatedDevice)
    {
        var existingDevice = _context.Devices
            .SingleOrDefault(x => x.Id == id);

        if (existingDevice == null)
        {
            return false;
        }

        existingDevice.Name = updatedDevice.Name;
        existingDevice.DeviceIdentifier = updatedDevice.DeviceIdentifier;
        existingDevice.DeviceType = updatedDevice.DeviceType;
        existingDevice.MapId = updatedDevice.MapId;
        existingDevice.Description = updatedDevice.Description;
        existingDevice.IsActive = updatedDevice.IsActive;
        existingDevice.ModifiedDate = updatedDevice.ModifiedDate;

        _context.SaveChanges();
        return true;
    }

    public bool DeleteDevice(int id)
    {
        var existingDevice = _context.Devices
            .SingleOrDefault(x => x.Id == id);

        if (existingDevice == null)
        {
            return false;
        }

        _context.Devices.Remove(existingDevice);
        _context.SaveChanges();
        return true;
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Device?> GetDeviceByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetDeviceById(id));
}
