using Npgsql;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Models;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class DeviceStatusRepository : IDeviceStatusDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public DeviceStatusRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<DeviceStatus> GetDeviceStatuses()
    {
        return _repo.ExecuteReader<DeviceStatus>(
            ConnectionString,
            @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
              FROM public.devicestatus
              ORDER BY id;");
    }

    public DeviceStatus? GetDeviceStatusById(int id)
    {
        return _repo.ExecuteReader<DeviceStatus>(
            ConnectionString,
            @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
              FROM public.devicestatus
              WHERE id = @id;",
            new[]
            {
                new NpgsqlParameter("id", id)
            })
            .SingleOrDefault();
    }

    public DeviceStatus? GetDeviceStatusByDeviceId(int deviceId)
    {
        return _repo.ExecuteReader<DeviceStatus>(
            ConnectionString,
            @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
              FROM public.devicestatus
              WHERE deviceid = @deviceId
              ORDER BY id;",
            new[]
            {
                new NpgsqlParameter("deviceId", deviceId)
            })
            .FirstOrDefault();
    }

    public bool DeviceStatusExistsByDeviceId(int deviceId, int? excludeId = null)
    {
        var sql = @"SELECT id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate
                    FROM public.devicestatus
                    WHERE deviceid = @deviceId";

        var parameters = new List<NpgsqlParameter>
        {
            new("deviceId", deviceId)
        };

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
            parameters.Add(new NpgsqlParameter("excludeId", excludeId.Value));
        }

        sql += ";";

        return _repo.ExecuteReader<DeviceStatus>(
            ConnectionString,
            sql,
            parameters.ToArray())
            .Any();
    }

    public DeviceStatus InsertDeviceStatus(DeviceStatus newDeviceStatus)
    {
        return _repo.ExecuteReader<DeviceStatus>(
            ConnectionString,
            @"INSERT INTO public.devicestatus
              (deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate)
              VALUES (@deviceId, @connectionState, @operationalState, @lastSeenAtUtc, @lastHeartbeatAtUtc, @poseMapId, @gridX, @gridY, @facing, @isGridAligned, @isGridPoseTrusted, @poseConfidence, @estimatedXcm, @estimatedYcm, @estimatedHeadingDegrees, @isInsideMap, @statusMessage, @lastErrorCode, @lastErrorMessage, @createdDate, @modifiedDate)
              RETURNING id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate;",
            BuildParameters(newDeviceStatus))
            .Single();
    }

    public bool UpdateDeviceStatus(int id, DeviceStatus updatedDeviceStatus)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("id", id)
        };

        parameters.AddRange(BuildParameters(updatedDeviceStatus));

        var result = _repo.ExecuteReader<DeviceStatus>(
            ConnectionString,
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
              WHERE id = @id
              RETURNING id, deviceid, connectionstate, operationalstate, lastseenatutc, lastheartbeatatutc, posemapid, gridx, gridy, facing, isgridaligned, isgridposetrusted, poseconfidence, estimatedxcm, estimatedycm, estimatedheadingdegrees, isinsidemap, statusmessage, lasterrorcode, lasterrormessage, createddate, modifieddate;",
            parameters.ToArray());

        return result.Any();
    }

    private static NpgsqlParameter[] BuildParameters(DeviceStatus model)
    {
        return new[]
        {
            new NpgsqlParameter("deviceId", model.DeviceId),
            new NpgsqlParameter("connectionState", model.ConnectionState),
            new NpgsqlParameter("operationalState", model.OperationalState),
            new NpgsqlParameter("lastSeenAtUtc", model.LastSeenAtUtc ?? (object)DBNull.Value),
            new NpgsqlParameter("lastHeartbeatAtUtc", model.LastHeartbeatAtUtc ?? (object)DBNull.Value),
            new NpgsqlParameter("poseMapId", model.PoseMapId ?? (object)DBNull.Value),
            new NpgsqlParameter("gridX", model.GridX ?? (object)DBNull.Value),
            new NpgsqlParameter("gridY", model.GridY ?? (object)DBNull.Value),
            new NpgsqlParameter("facing", model.Facing ?? (object)DBNull.Value),
            new NpgsqlParameter("isGridAligned", model.IsGridAligned),
            new NpgsqlParameter("isGridPoseTrusted", model.IsGridPoseTrusted),
            new NpgsqlParameter("poseConfidence", model.PoseConfidence ?? (object)DBNull.Value),
            new NpgsqlParameter("estimatedXcm", model.EstimatedXcm ?? (object)DBNull.Value),
            new NpgsqlParameter("estimatedYcm", model.EstimatedYcm ?? (object)DBNull.Value),
            new NpgsqlParameter("estimatedHeadingDegrees", model.EstimatedHeadingDegrees ?? (object)DBNull.Value),
            new NpgsqlParameter("isInsideMap", model.IsInsideMap ?? (object)DBNull.Value),
            new NpgsqlParameter("statusMessage", model.StatusMessage ?? (object)DBNull.Value),
            new NpgsqlParameter("lastErrorCode", model.LastErrorCode ?? (object)DBNull.Value),
            new NpgsqlParameter("lastErrorMessage", model.LastErrorMessage ?? (object)DBNull.Value),
            new NpgsqlParameter("createdDate", model.CreatedDate),
            new NpgsqlParameter("modifiedDate", model.ModifiedDate)
        };
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<DeviceStatus?> GetDeviceStatusByDeviceIdAsync(int deviceId, CancellationToken ct = default) => Task.FromResult(GetDeviceStatusByDeviceId(deviceId));
    public Task<bool> UpdateDeviceStatusAsync(int id, DeviceStatus updatedDeviceStatus, CancellationToken ct = default) => Task.FromResult(UpdateDeviceStatus(id, updatedDeviceStatus));
}
