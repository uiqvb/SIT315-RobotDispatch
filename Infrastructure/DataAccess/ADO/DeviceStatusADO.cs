using Npgsql;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Models;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class DeviceStatusADO : IDeviceStatusDataAccess
{
    private readonly DbConfig _dbConfig;

    public DeviceStatusADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<DeviceStatus> GetDeviceStatuses()
    {
        var results = new List<DeviceStatus>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
              FROM public.devicestatus
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapDeviceStatus(dr));
        }

        return results;
    }

    public DeviceStatus? GetDeviceStatusById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
              FROM public.devicestatus
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapDeviceStatus(dr) : null;
    }

    private const string GetDeviceStatusByDeviceIdSql =
        @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
              FROM public.devicestatus
              WHERE deviceid = @deviceId
              ORDER BY id;";

    public DeviceStatus? GetDeviceStatusByDeviceId(int deviceId)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(GetDeviceStatusByDeviceIdSql, conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        return dr.Read() ? MapDeviceStatus(dr) : null;
    }

    public Task<DeviceStatus?> GetDeviceStatusByDeviceIdAsync(int deviceId, CancellationToken ct = default) =>
        AdoAsync.QuerySingleAsync(_dbConfig, GetDeviceStatusByDeviceIdSql, cmd => cmd.Parameters.AddWithValue("deviceId", deviceId), MapDeviceStatus, ct);

    public bool DeviceStatusExistsByDeviceId(int deviceId, int? excludeId = null)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        var sql = @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
                    FROM public.devicestatus
                    WHERE deviceid = @deviceId";

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
        }

        sql += ";";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("deviceId", deviceId);

        if (excludeId.HasValue)
        {
            cmd.Parameters.AddWithValue("excludeId", excludeId.Value);
        }

        using var dr = cmd.ExecuteReader();
        return dr.Read();
    }

    public DeviceStatus InsertDeviceStatus(DeviceStatus newDeviceStatus)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.devicestatus
              (deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate)
              VALUES (@deviceId, @connectionState, @operationalState, @lastSeenAtUtc, @lastHeartbeatAtUtc, @poseMapId, @gridX, @gridY, @facing, @isGridAligned, @isGridPoseTrusted, @poseConfidence, @estimatedXcm, @estimatedYcm, @estimatedHeadingDegrees, @isInsideMap, @statusMessage, @lastErrorCode, @lastErrorMessage, @createdDate, @modifiedDate)
              RETURNING id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate;", conn);

        AddParameters(cmd, newDeviceStatus);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapDeviceStatus(dr);
        }

        throw new InvalidOperationException("DeviceStatus insert failed.");
    }

    private const string UpdateDeviceStatusSql =
        @"UPDATE public.devicestatus
              SET deviceid = @deviceId,
                  connectionstate = @connectionState,
                  operationalstate = @operationalState,
                  lastseenatutc = @lastSeenAtUtc,
                  lastheartbeatatutc = @lastHeartbeatAtUtc,
                  posemapid = @poseMapId,
                  gridx = @gridX,
                  gridy = @gridY,
                  facing = @facing,
                  isgridaligned = @isGridAligned,
                  isgridposetrusted = @isGridPoseTrusted,
                  poseconfidence = @poseConfidence,
                  estimatedxcm = @estimatedXcm,
                  estimatedycm = @estimatedYcm,
                  estimatedheadingdegrees = @estimatedHeadingDegrees,
                  isinsidemap = @isInsideMap,
                  statusmessage = @statusMessage,
                  lasterrorcode = @lastErrorCode,
                  lasterrormessage = @lastErrorMessage,
                  modifieddate = @modifiedDate
              WHERE id = @id;";

    public bool UpdateDeviceStatus(int id, DeviceStatus updatedDeviceStatus)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(UpdateDeviceStatusSql, conn);

        cmd.Parameters.AddWithValue("id", id);
        AddParameters(cmd, updatedDeviceStatus);

        return cmd.ExecuteNonQuery() > 0;
    }

    public async Task<bool> UpdateDeviceStatusAsync(int id, DeviceStatus updatedDeviceStatus, CancellationToken ct = default) =>
        await AdoAsync.ExecuteAsync(_dbConfig, UpdateDeviceStatusSql, cmd =>
        {
            cmd.Parameters.AddWithValue("id", id);
            AddParameters(cmd, updatedDeviceStatus);
        }, ct) > 0;

    private static void AddParameters(NpgsqlCommand cmd, DeviceStatus model)
    {
        cmd.Parameters.AddWithValue("deviceId", model.DeviceId);
        cmd.Parameters.AddWithValue("connectionState", model.ConnectionState);
        cmd.Parameters.AddWithValue("operationalState", model.OperationalState);
        cmd.Parameters.AddWithValue("lastSeenAtUtc", model.LastSeenAtUtc ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("lastHeartbeatAtUtc", model.LastHeartbeatAtUtc ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("poseMapId", model.PoseMapId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("gridX", model.GridX ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("gridY", model.GridY ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("facing", model.Facing ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isGridAligned", model.IsGridAligned);
        cmd.Parameters.AddWithValue("isGridPoseTrusted", model.IsGridPoseTrusted);
        cmd.Parameters.AddWithValue("poseConfidence", model.PoseConfidence ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("estimatedXcm", model.EstimatedXcm ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("estimatedYcm", model.EstimatedYcm ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("estimatedHeadingDegrees", model.EstimatedHeadingDegrees ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isInsideMap", model.IsInsideMap ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("statusMessage", model.StatusMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("lastErrorCode", model.LastErrorCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("lastErrorMessage", model.LastErrorMessage ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("createdDate", model.CreatedDate);
        cmd.Parameters.AddWithValue("modifiedDate", model.ModifiedDate);
    }

    private static DeviceStatus MapDeviceStatus(NpgsqlDataReader dr)
    {
        return new DeviceStatus
        {
            Id = dr.GetInt32(0),
            DeviceId = dr.GetInt32(1),
            ConnectionState = dr.GetString(2),
            OperationalState = dr.GetString(3),
            LastSeenAtUtc = dr.IsDBNull(4) ? null : dr.GetDateTime(4),
            LastHeartbeatAtUtc = dr.IsDBNull(5) ? null : dr.GetDateTime(5),
            PoseMapId = dr.IsDBNull(6) ? null : dr.GetInt32(6),
            GridX = dr.IsDBNull(7) ? null : dr.GetInt32(7),
            GridY = dr.IsDBNull(8) ? null : dr.GetInt32(8),
            Facing = dr.IsDBNull(9) ? null : dr.GetString(9),
            IsGridAligned = dr.GetBoolean(10),
            IsGridPoseTrusted = dr.GetBoolean(11),
            PoseConfidence = dr.IsDBNull(12) ? null : dr.GetDouble(12),
            EstimatedXcm = dr.IsDBNull(13) ? null : dr.GetDouble(13),
            EstimatedYcm = dr.IsDBNull(14) ? null : dr.GetDouble(14),
            EstimatedHeadingDegrees = dr.IsDBNull(15) ? null : dr.GetDouble(15),
            IsInsideMap = dr.IsDBNull(16) ? null : dr.GetBoolean(16),
            StatusMessage = dr.IsDBNull(17) ? null : dr.GetString(17),
            LastErrorCode = dr.IsDBNull(18) ? null : dr.GetString(18),
            LastErrorMessage = dr.IsDBNull(19) ? null : dr.GetString(19),
            CreatedDate = dr.GetDateTime(20),
            ModifiedDate = dr.GetDateTime(21)
        };
    }
}
