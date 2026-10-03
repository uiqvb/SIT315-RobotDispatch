using Npgsql;
using RobotControllerApi.BoundedContexts.Devices.Models;
using RobotControllerApi.BoundedContexts.Devices.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class DeviceADO : IDeviceDataAccess
{
    private readonly DbConfig _dbConfig;

    public DeviceADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<Device> GetDevices()
    {
        var results = new List<Device>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate
              FROM public.device
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapDevice(dr));
        }

        return results;
    }

    private const string GetDeviceByIdSql =
        @"SELECT id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate
              FROM public.device
              WHERE id = @id;";

    public Device? GetDeviceById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(GetDeviceByIdSql, conn);

        cmd.Parameters.AddWithValue("id", id);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapDevice(dr) : null;
    }

    public Task<Device?> GetDeviceByIdAsync(int id, CancellationToken ct = default) =>
        AdoAsync.QuerySingleAsync(_dbConfig, GetDeviceByIdSql, cmd => cmd.Parameters.AddWithValue("id", id), MapDevice, ct);

    public bool DeviceExistsByIdentifier(string deviceIdentifier, int? excludeId = null)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        var sql = @"SELECT id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate
                    FROM public.device
                    WHERE LOWER(deviceidentifier) = LOWER(@deviceIdentifier)";

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
        }

        sql += ";";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("deviceIdentifier", deviceIdentifier);

        if (excludeId.HasValue)
        {
            cmd.Parameters.AddWithValue("excludeId", excludeId.Value);
        }

        using var dr = cmd.ExecuteReader();
        return dr.Read();
    }

    public Device InsertDevice(Device newDevice)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.device
              (name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate)
              VALUES (@name, @deviceIdentifier, @deviceType, @mapId, @description, @isActive, @createdDate, @modifiedDate)
              RETURNING id, name, deviceidentifier, devicetype, mapid, description, isactive, createddate, modifieddate;", conn);

        cmd.Parameters.AddWithValue("name", newDevice.Name);
        cmd.Parameters.AddWithValue("deviceIdentifier", newDevice.DeviceIdentifier);
        cmd.Parameters.AddWithValue("deviceType", newDevice.DeviceType);
        cmd.Parameters.AddWithValue("mapId", newDevice.MapId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("description", newDevice.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isActive", newDevice.IsActive);
        cmd.Parameters.AddWithValue("createdDate", newDevice.CreatedDate);
        cmd.Parameters.AddWithValue("modifiedDate", newDevice.ModifiedDate);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapDevice(dr);
        }

        throw new InvalidOperationException("Device insert failed.");
    }

    public bool UpdateDevice(int id, Device updatedDevice)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"UPDATE public.device
              SET name = @name,
                  deviceidentifier = @deviceIdentifier,
                  devicetype = @deviceType,
                  mapid = @mapId,
                  description = @description,
                  isactive = @isActive,
                  modifieddate = @modifiedDate
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", updatedDevice.Name);
        cmd.Parameters.AddWithValue("deviceIdentifier", updatedDevice.DeviceIdentifier);
        cmd.Parameters.AddWithValue("deviceType", updatedDevice.DeviceType);
        cmd.Parameters.AddWithValue("mapId", updatedDevice.MapId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("description", updatedDevice.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isActive", updatedDevice.IsActive);
        cmd.Parameters.AddWithValue("modifiedDate", updatedDevice.ModifiedDate);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteDevice(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"DELETE FROM public.device
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        return cmd.ExecuteNonQuery() > 0;
    }

    private static Device MapDevice(NpgsqlDataReader dr)
    {
        return new Device
        {
            Id = dr.GetInt32(0),
            Name = dr.GetString(1),
            DeviceIdentifier = dr.GetString(2),
            DeviceType = dr.GetString(3),
            MapId = dr.IsDBNull(4) ? null : dr.GetInt32(4),
            Description = dr.IsDBNull(5) ? null : dr.GetString(5),
            IsActive = dr.GetBoolean(6),
            CreatedDate = dr.GetDateTime(7),
            ModifiedDate = dr.GetDateTime(8)
        };
    }
}
