using Npgsql;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Models;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class DeviceCredentialRepository : IDeviceCredentialDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public DeviceCredentialRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<DeviceCredential> GetDeviceCredentials()
        => _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              ORDER BY id;");

    public DeviceCredential? GetDeviceCredentialById(int id)
        => _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              WHERE id = @id;",
            new[] { new NpgsqlParameter("id", id) }
        ).SingleOrDefault();

    public List<DeviceCredential> GetDeviceCredentialsByDeviceId(int deviceId)
        => _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
            @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
              FROM public.devicecredential
              WHERE deviceid = @deviceId
              ORDER BY id;",
            new[] { new NpgsqlParameter("deviceId", deviceId) }
        );

    public bool DeviceCredentialExistsByCredentialIdentifier(string credentialIdentifier, int? excludeId = null)
    {
        var sql = @"SELECT id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate
                    FROM public.devicecredential
                    WHERE lower(credentialidentifier) = lower(@credentialIdentifier)";

        var parameters = new List<NpgsqlParameter>
        {
            new("credentialIdentifier", credentialIdentifier)
        };

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
            parameters.Add(new NpgsqlParameter("excludeId", excludeId.Value));
        }

        return _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
            sql,
            parameters.ToArray()).Any();
    }

    public DeviceCredential InsertDeviceCredential(DeviceCredential newDeviceCredential)
    {
        return _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
            @"INSERT INTO public.devicecredential
              (deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate)
              VALUES (@deviceId, @name, @credentialIdentifier, @secretKeyPrefix, @secretHash, @hashAlgorithm, @expiresAtUtc, @lastUsedAtUtc, @lastUsedIpAddress, @lastUsedUserAgent, @revokedAtUtc, @revocationReason, @isActive, @createdDate, @modifiedDate)
              RETURNING id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate;",
            new[]
            {
                new NpgsqlParameter("deviceId", newDeviceCredential.DeviceId),
                new NpgsqlParameter("name", newDeviceCredential.Name),
                new NpgsqlParameter("credentialIdentifier", newDeviceCredential.CredentialIdentifier),
                new NpgsqlParameter("secretKeyPrefix", newDeviceCredential.SecretKeyPrefix),
                new NpgsqlParameter("secretHash", newDeviceCredential.SecretHash),
                new NpgsqlParameter("hashAlgorithm", newDeviceCredential.HashAlgorithm),
                new NpgsqlParameter("expiresAtUtc", (object?)newDeviceCredential.ExpiresAtUtc ?? DBNull.Value),
                new NpgsqlParameter("lastUsedAtUtc", (object?)newDeviceCredential.LastUsedAtUtc ?? DBNull.Value),
                new NpgsqlParameter("lastUsedIpAddress", newDeviceCredential.LastUsedIpAddress ?? (object)DBNull.Value),
                new NpgsqlParameter("lastUsedUserAgent", newDeviceCredential.LastUsedUserAgent ?? (object)DBNull.Value),
                new NpgsqlParameter("revokedAtUtc", (object?)newDeviceCredential.RevokedAtUtc ?? DBNull.Value),
                new NpgsqlParameter("revocationReason", newDeviceCredential.RevocationReason ?? (object)DBNull.Value),
                new NpgsqlParameter("isActive", newDeviceCredential.IsActive),
                new NpgsqlParameter("createdDate", newDeviceCredential.CreatedDate),
                new NpgsqlParameter("modifiedDate", newDeviceCredential.ModifiedDate)
            }
        ).Single();
    }

    public bool UpdateDeviceCredential(int id, DeviceCredential updatedDeviceCredential)
    {
        var result = _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
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
              WHERE id = @id
              RETURNING id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate;",
            new[]
            {
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("deviceId", updatedDeviceCredential.DeviceId),
                new NpgsqlParameter("name", updatedDeviceCredential.Name),
                new NpgsqlParameter("credentialIdentifier", updatedDeviceCredential.CredentialIdentifier),
                new NpgsqlParameter("secretKeyPrefix", updatedDeviceCredential.SecretKeyPrefix),
                new NpgsqlParameter("secretHash", updatedDeviceCredential.SecretHash),
                new NpgsqlParameter("hashAlgorithm", updatedDeviceCredential.HashAlgorithm),
                new NpgsqlParameter("expiresAtUtc", (object?)updatedDeviceCredential.ExpiresAtUtc ?? DBNull.Value),
                new NpgsqlParameter("lastUsedAtUtc", (object?)updatedDeviceCredential.LastUsedAtUtc ?? DBNull.Value),
                new NpgsqlParameter("lastUsedIpAddress", updatedDeviceCredential.LastUsedIpAddress ?? (object)DBNull.Value),
                new NpgsqlParameter("lastUsedUserAgent", updatedDeviceCredential.LastUsedUserAgent ?? (object)DBNull.Value),
                new NpgsqlParameter("revokedAtUtc", (object?)updatedDeviceCredential.RevokedAtUtc ?? DBNull.Value),
                new NpgsqlParameter("revocationReason", updatedDeviceCredential.RevocationReason ?? (object)DBNull.Value),
                new NpgsqlParameter("isActive", updatedDeviceCredential.IsActive),
                new NpgsqlParameter("modifiedDate", updatedDeviceCredential.ModifiedDate)
            }
        );

        return result.Any();
    }

    public bool DeleteDeviceCredential(int id)
    {
        var result = _repo.ExecuteReader<DeviceCredential>(
            ConnectionString,
            @"DELETE FROM public.devicecredential
              WHERE id = @id
              RETURNING id, deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, expiresatutc, lastusedatutc, lastusedipaddress, lastuseduseragent, revokedatutc, revocationreason, isactive, createddate, modifieddate;",
            new[] { new NpgsqlParameter("id", id) }
        );

        return result.Any();
    }

    // SIT315 targeted auth lookup and usage write are implemented for the ADO provider only.
    private const string AdoOnly = "Targeted device credential access is only implemented for the ADO persistence provider.";
    public DeviceCredential? GetDeviceCredentialByCredentialIdentifier(string credentialIdentifier) => throw new NotSupportedException(AdoOnly);
    public bool UpdateDeviceCredentialLastUsed(int id, DateTime lastUsedAtUtc, string? lastUsedIpAddress, string? lastUsedUserAgent) => throw new NotSupportedException(AdoOnly);
}
