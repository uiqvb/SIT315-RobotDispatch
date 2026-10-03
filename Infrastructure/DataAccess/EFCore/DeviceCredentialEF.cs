using RobotControllerApi.BoundedContexts.DeviceCredentials.Models;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class DeviceCredentialEF : IDeviceCredentialDataAccess
{
    private readonly RobotContext _context;

    public DeviceCredentialEF(RobotContext context)
    {
        _context = context;
    }

    public List<DeviceCredential> GetDeviceCredentials()
    {
        return _context.DeviceCredentials
            .OrderBy(x => x.Id)
            .ToList();
    }

    public DeviceCredential? GetDeviceCredentialById(int id)
    {
        return _context.DeviceCredentials
            .SingleOrDefault(x => x.Id == id);
    }

    public List<DeviceCredential> GetDeviceCredentialsByDeviceId(int deviceId)
    {
        return _context.DeviceCredentials
            .Where(x => x.DeviceId == deviceId)
            .OrderBy(x => x.Id)
            .ToList();
    }

    public bool DeviceCredentialExistsByCredentialIdentifier(string credentialIdentifier, int? excludeId = null)
    {
        var query = _context.DeviceCredentials
            .Where(x => x.CredentialIdentifier.ToLower() == credentialIdentifier.ToLower());

        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }

        return query.Any();
    }

    public DeviceCredential InsertDeviceCredential(DeviceCredential newDeviceCredential)
    {
        _context.DeviceCredentials.Add(newDeviceCredential);
        _context.SaveChanges();
        return newDeviceCredential;
    }

    public bool UpdateDeviceCredential(int id, DeviceCredential updatedDeviceCredential)
    {
        var existingDeviceCredential = _context.DeviceCredentials
            .SingleOrDefault(x => x.Id == id);

        if (existingDeviceCredential == null)
        {
            return false;
        }

        existingDeviceCredential.DeviceId = updatedDeviceCredential.DeviceId;
        existingDeviceCredential.Name = updatedDeviceCredential.Name;
        existingDeviceCredential.CredentialIdentifier = updatedDeviceCredential.CredentialIdentifier;
        existingDeviceCredential.SecretKeyPrefix = updatedDeviceCredential.SecretKeyPrefix;
        existingDeviceCredential.SecretHash = updatedDeviceCredential.SecretHash;
        existingDeviceCredential.HashAlgorithm = updatedDeviceCredential.HashAlgorithm;
        existingDeviceCredential.ExpiresAtUtc = updatedDeviceCredential.ExpiresAtUtc;
        existingDeviceCredential.LastUsedAtUtc = updatedDeviceCredential.LastUsedAtUtc;
        existingDeviceCredential.LastUsedIpAddress = updatedDeviceCredential.LastUsedIpAddress;
        existingDeviceCredential.LastUsedUserAgent = updatedDeviceCredential.LastUsedUserAgent;
        existingDeviceCredential.RevokedAtUtc = updatedDeviceCredential.RevokedAtUtc;
        existingDeviceCredential.RevocationReason = updatedDeviceCredential.RevocationReason;
        existingDeviceCredential.IsActive = updatedDeviceCredential.IsActive;
        existingDeviceCredential.ModifiedDate = updatedDeviceCredential.ModifiedDate;

        _context.SaveChanges();
        return true;
    }

    public bool DeleteDeviceCredential(int id)
    {
        var existingDeviceCredential = _context.DeviceCredentials
            .SingleOrDefault(x => x.Id == id);

        if (existingDeviceCredential == null)
        {
            return false;
        }

        _context.DeviceCredentials.Remove(existingDeviceCredential);
        _context.SaveChanges();
        return true;
    }

    // SIT315 targeted auth lookup and usage write are implemented for the ADO provider only.
    private const string AdoOnly = "Targeted device credential access is only implemented for the ADO persistence provider.";
    public DeviceCredential? GetDeviceCredentialByCredentialIdentifier(string credentialIdentifier) => throw new NotSupportedException(AdoOnly);
    public bool UpdateDeviceCredentialLastUsed(int id, DateTime lastUsedAtUtc, string? lastUsedIpAddress, string? lastUsedUserAgent) => throw new NotSupportedException(AdoOnly);
}
