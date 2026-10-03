using Npgsql;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Models;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class DeviceCredentialADO : IDeviceCredentialDataAccess
{
    private readonly DbConfig _dbConfig;

    public DeviceCredentialADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<DeviceCredential> GetDeviceCredentials()
    {
        var deviceCredentials = new List<DeviceCredential>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            deviceCredentials.Add(MapDeviceCredential(dr));
        }

        return deviceCredentials;
    }

    public DeviceCredential? GetDeviceCredentialById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapDeviceCredential(dr);
        }

        return null;
    }

    public List<DeviceCredential> GetDeviceCredentialsByDeviceId(int deviceId)
    {
        var deviceCredentials = new List<DeviceCredential>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              WHERE deviceid = @deviceId
              ORDER BY id;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            deviceCredentials.Add(MapDeviceCredential(dr));
        }

        return deviceCredentials;
    }

    public bool DeviceCredentialExistsByCredentialIdentifier(string credentialIdentifier, int? excludeId = null)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        var sql = @"SELECT id
                    FROM public.devicecredential
                    WHERE lower(credentialidentifier) = lower(@credentialIdentifier)";

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
        }

        using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("credentialIdentifier", credentialIdentifier);

        if (excludeId.HasValue)
        {
            cmd.Parameters.AddWithValue("excludeId", excludeId.Value);
        }

        using var dr = cmd.ExecuteReader();
        return dr.Read();
    }

    public DeviceCredential InsertDeviceCredential(DeviceCredential newDeviceCredential)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.devicecredential
              (deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate)
              VALUES (@deviceId, @name, @credentialIdentifier, @secretKeyPrefix, @secretHash, @hashAlgorithm, @expiresAtUtc, @lastUsedAtUtc, @lastUsedIpAddress, @lastUsedUserAgent, @revokedAtUtc, @revocationReason, @isActive, @createdDate, @modifiedDate)
              RETURNING id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate;", conn);

        cmd.Parameters.AddWithValue("deviceId", newDeviceCredential.DeviceId);
        cmd.Parameters.AddWithValue("name", newDeviceCredential.Name);
        cmd.Parameters.AddWithValue("credentialIdentifier", newDeviceCredential.CredentialIdentifier);
        cmd.Parameters.AddWithValue("secretKeyPrefix", newDeviceCredential.SecretKeyPrefix);
        cmd.Parameters.AddWithValue("secretHash", newDeviceCredential.SecretHash);
        cmd.Parameters.AddWithValue("hashAlgorithm", newDeviceCredential.HashAlgorithm);
        cmd.Parameters.AddWithValue("expiresAtUtc", (object?)newDeviceCredential.ExpiresAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedAtUtc", (object?)newDeviceCredential.LastUsedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedIpAddress", newDeviceCredential.LastUsedIpAddress ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedUserAgent", newDeviceCredential.LastUsedUserAgent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("revokedAtUtc", (object?)newDeviceCredential.RevokedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("revocationReason", newDeviceCredential.RevocationReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isActive", newDeviceCredential.IsActive);
        cmd.Parameters.AddWithValue("createdDate", newDeviceCredential.CreatedDate);
        cmd.Parameters.AddWithValue("modifiedDate", newDeviceCredential.ModifiedDate);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapDeviceCredential(dr);
        }

        throw new InvalidOperationException("Device credential insert failed.");
    }

    public bool UpdateDeviceCredential(int id, DeviceCredential updatedDeviceCredential)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"UPDATE public.devicecredential
              SET deviceid = @deviceId,
                  name = @name,
                  credentialidentifier = @credentialIdentifier,
                  secretkeyprefix = @secretKeyPrefix,
                  secrethash = @secretHash,
                  hashalgorithm = @hashAlgorithm,
                  expiresatutc = @expiresAtUtc,
                  lastusedatutc = @lastUsedAtUtc,
                  lastusedipaddress = @lastUsedIpAddress,
                  lastuseduseragent = @lastUsedUserAgent,
                  revokedatutc = @revokedAtUtc,
                  revocationreason = @revocationReason,
                  isactive = @isActive,
                  modifieddate = @modifiedDate
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("deviceId", updatedDeviceCredential.DeviceId);
        cmd.Parameters.AddWithValue("name", updatedDeviceCredential.Name);
        cmd.Parameters.AddWithValue("credentialIdentifier", updatedDeviceCredential.CredentialIdentifier);
        cmd.Parameters.AddWithValue("secretKeyPrefix", updatedDeviceCredential.SecretKeyPrefix);
        cmd.Parameters.AddWithValue("secretHash", updatedDeviceCredential.SecretHash);
        cmd.Parameters.AddWithValue("hashAlgorithm", updatedDeviceCredential.HashAlgorithm);
        cmd.Parameters.AddWithValue("expiresAtUtc", (object?)updatedDeviceCredential.ExpiresAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedAtUtc", (object?)updatedDeviceCredential.LastUsedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedIpAddress", updatedDeviceCredential.LastUsedIpAddress ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedUserAgent", updatedDeviceCredential.LastUsedUserAgent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("revokedAtUtc", (object?)updatedDeviceCredential.RevokedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("revocationReason", updatedDeviceCredential.RevocationReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("isActive", updatedDeviceCredential.IsActive);
        cmd.Parameters.AddWithValue("modifiedDate", updatedDeviceCredential.ModifiedDate);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteDeviceCredential(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"DELETE FROM public.devicecredential
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("id", id);

        return cmd.ExecuteNonQuery() > 0;
    }

    public Task<DeviceCredential?> GetDeviceCredentialByCredentialIdentifierAsync(string credentialIdentifier, CancellationToken ct = default) =>
        // Matches ux_devicecredential_identifier_lower, so this is one index lookup rather than a table load.
        AdoAsync.QuerySingleAsync(_dbConfig,
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              WHERE lower(credentialidentifier) = lower(@credentialIdentifier);",
            cmd => cmd.Parameters.AddWithValue("credentialIdentifier", credentialIdentifier), MapDeviceCredential, ct);

    public async Task<bool> UpdateDeviceCredentialLastUsedAsync(int id, DateTime lastUsedAtUtc, string? lastUsedIpAddress, string? lastUsedUserAgent, CancellationToken ct = default) =>
        // Touches only the usage columns, so it cannot write back a stale isactive or revokedatutc.
        await AdoAsync.ExecuteAsync(_dbConfig,
            @"UPDATE public.devicecredential
              SET lastusedatutc = @lastUsedAtUtc,
                  lastusedipaddress = @lastUsedIpAddress,
                  lastuseduseragent = @lastUsedUserAgent,
                  modifieddate = @lastUsedAtUtc
              WHERE id = @id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("lastUsedAtUtc", lastUsedAtUtc);
                cmd.Parameters.AddWithValue("lastUsedIpAddress", lastUsedIpAddress ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("lastUsedUserAgent", lastUsedUserAgent ?? (object)DBNull.Value);
            }, ct) > 0;

    private static DeviceCredential MapDeviceCredential(NpgsqlDataReader dr)
    {
        return new DeviceCredential
        {
            Id = dr.GetInt32(0),
            DeviceId = dr.GetInt32(1),
            Name = dr.GetString(2),
            CredentialIdentifier = dr.GetString(3),
            SecretKeyPrefix = dr.GetString(4),
            SecretHash = dr.GetString(5),
            HashAlgorithm = dr.GetString(6),
            ExpiresAtUtc = dr.IsDBNull(7) ? null : dr.GetDateTime(7),
            LastUsedAtUtc = dr.IsDBNull(8) ? null : dr.GetDateTime(8),
            LastUsedIpAddress = dr.IsDBNull(9) ? null : dr.GetString(9),
            LastUsedUserAgent = dr.IsDBNull(10) ? null : dr.GetString(10),
            RevokedAtUtc = dr.IsDBNull(11) ? null : dr.GetDateTime(11),
            RevocationReason = dr.IsDBNull(12) ? null : dr.GetString(12),
            IsActive = dr.GetBoolean(13),
            CreatedDate = dr.GetDateTime(14),
            ModifiedDate = dr.GetDateTime(15)
        };
    }
}
