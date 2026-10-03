using Npgsql;
using RobotControllerApi.BoundedContexts.Devices.Models;
using RobotControllerApi.BoundedContexts.Devices.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class DeviceRepository : IDeviceDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public DeviceRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<Device> GetDevices()
        => _repo.ExecuteReader<Device>(
            ConnectionString,
            @"SELECT id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate
              FROM public.device
              ORDER BY id;");

    public Device? GetDeviceById(int id)
        => _repo.ExecuteReader<Device>(
            ConnectionString,
            @"SELECT id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate
              FROM public.device
              WHERE id = @id;",
            new[] { new NpgsqlParameter("id", id) }
        ).SingleOrDefault();

    public bool DeviceExistsByIdentifier(string deviceIdentifier, int? excludeId = null)
        => GetDevices().Any(x =>
            x.DeviceIdentifier.Equals(deviceIdentifier, StringComparison.OrdinalIgnoreCase) &&
            (!excludeId.HasValue || x.Id != excludeId.Value));

    public Device InsertDevice(Device newDevice)
    {
        var result = _repo.ExecuteReader<Device>(
            ConnectionString,
            @"INSERT INTO public.device
              (name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate)
              VALUES (@name, @deviceIdentifier, @deviceType, @mapId, @description, @isActive, @createdDate, @modifiedDate)
              RETURNING id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate;",
            new[]
            {
                new NpgsqlParameter("name", newDevice.Name),
                new NpgsqlParameter("deviceIdentifier", newDevice.DeviceIdentifier),
                new NpgsqlParameter("deviceType", newDevice.DeviceType),
                new NpgsqlParameter("mapId", newDevice.MapId ?? (object)DBNull.Value),
                new NpgsqlParameter("description", newDevice.Description ?? (object)DBNull.Value),
                new NpgsqlParameter("isActive", newDevice.IsActive),
                new NpgsqlParameter("createdDate", newDevice.CreatedDate),
                new NpgsqlParameter("modifiedDate", newDevice.ModifiedDate)
            }
        ).Single();

        return result;
    }

    public bool UpdateDevice(int id, Device updatedDevice)
    {
        var result = _repo.ExecuteReader<Device>(
            ConnectionString,
            @"UPDATE public.device
              SET name = @name,
                  deviceidentifier = @deviceIdentifier,
                  devicetype = @deviceType,
                  mapid = @mapId,
                  description = @description,
                  isactive = @isActive,
                  modifieddate = @modifiedDate
              WHERE id = @id
              RETURNING id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate;",
            new[]
            {
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("name", updatedDevice.Name),
                new NpgsqlParameter("deviceIdentifier", updatedDevice.DeviceIdentifier),
                new NpgsqlParameter("deviceType", updatedDevice.DeviceType),
                new NpgsqlParameter("mapId", updatedDevice.MapId ?? (object)DBNull.Value),
                new NpgsqlParameter("description", updatedDevice.Description ?? (object)DBNull.Value),
                new NpgsqlParameter("isActive", updatedDevice.IsActive),
                new NpgsqlParameter("modifiedDate", updatedDevice.ModifiedDate)
            }
        );

        return result.Any();
    }

    public bool DeleteDevice(int id)
    {
        var result = _repo.ExecuteReader<Device>(
            ConnectionString,
            @"DELETE FROM public.device
              WHERE id = @id
              RETURNING id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate;",
            new[] { new NpgsqlParameter("id", id) }
        );

        return result.Any();
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Device?> GetDeviceByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetDeviceById(id));
}
