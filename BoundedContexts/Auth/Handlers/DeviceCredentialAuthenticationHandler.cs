using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using RobotControllerApi.BoundedContexts.Auth.Constants;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Models;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Services;
using RobotControllerApi.BoundedContexts.Devices.Persistence;

namespace RobotControllerApi.BoundedContexts.Auth.Handlers;

public class DeviceCredentialAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string CredentialIdHeader = "X-Device-Credential-Id";
    private const string CredentialSecretHeader = "X-Device-Credential-Secret";

    private readonly IDeviceCredentialDataAccess _credentials;
    private readonly IDeviceDataAccess _devices;
    private readonly MultiDeviceCredentialSecretHashService _secretHasher;
    private readonly IConfiguration _configuration;

    public DeviceCredentialAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDeviceCredentialDataAccess credentials,
        IDeviceDataAccess devices,
        MultiDeviceCredentialSecretHashService secretHasher,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _credentials = credentials;
        _devices = devices;
        _secretHasher = secretHasher;
        _configuration = configuration;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ct = Context.RequestAborted; //robot hangs up -> its DB calls are cancelled too
        var endpoint = Context.GetEndpoint();

        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            return AuthenticateResult.NoResult();
        }

        Response.Headers["WWW-Authenticate"] = "DeviceCredential";

        if (!Request.Headers.TryGetValue(CredentialIdHeader, out var credentialIdentifierValues)
            || !Request.Headers.TryGetValue(CredentialSecretHeader, out var secretValues))
        {
            return FailAuthentication();
        }

        var credentialIdentifier = credentialIdentifierValues.ToString().Trim();
        var rawSecret = secretValues.ToString();

        if (string.IsNullOrWhiteSpace(credentialIdentifier) || string.IsNullOrWhiteSpace(rawSecret))
        {
            return FailAuthentication();
        }

        var credential = await _credentials.GetDeviceCredentialByCredentialIdentifierAsync(credentialIdentifier, ct); //one row by identifier, not the whole table; thread freed while PostgreSQL works

        if (credential == null)
        {
            return FailAuthentication();
        }

        if (!credential.IsActive || credential.RevokedAtUtc.HasValue)
        {
            return FailAuthentication();
        }

        if (credential.ExpiresAtUtc.HasValue && credential.ExpiresAtUtc.Value <= DateTime.UtcNow)
        {
            return FailAuthentication();
        }

        if (!_secretHasher.VerifySecret(rawSecret, credential.SecretHash, credential.HashAlgorithm))
        {
            return FailAuthentication();
        }

        var device = await _devices.GetDeviceByIdAsync(credential.DeviceId, ct);

        if (device == null || !device.IsActive)
        {
            return FailAuthentication();
        }

        if (RouteHasDeviceId(out var routeDeviceId) && routeDeviceId != credential.DeviceId)
        {
            return FailAuthentication();
        }

        await PersistLastUsedIfDueAsync(credential, ct); //usage metadata only; auth has already succeeded and does not depend on this write

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, credential.Id.ToString()),
            new(AuthClaimTypes.ActorType, AuthClaimTypes.DeviceAdapterActor),
            new(AuthClaimTypes.DeviceCredentialId, credential.Id.ToString()),
            new(AuthClaimTypes.DeviceId, credential.DeviceId.ToString()),
            new(AuthClaimTypes.CredentialIdentifier, credential.CredentialIdentifier),
            new(ClaimTypes.Name, credential.Name)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    // Usage metadata is written at most once per interval, so a polling robot does not turn every request into a write.
    private async Task PersistLastUsedIfDueAsync(DeviceCredential credential, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var intervalMinutes = int.TryParse(_configuration["DeviceAuth:LastUsedPersistIntervalMinutes"], out var parsed) && parsed >= 0 ? parsed : 10;

        if (credential.LastUsedAtUtc.HasValue && now - credential.LastUsedAtUtc.Value < TimeSpan.FromMinutes(intervalMinutes)) //the row we just read says it was written recently
        {
            return;
        }

        try
        {
            await _credentials.UpdateDeviceCredentialLastUsedAsync( //writes only the three usage columns
                credential.Id,
                now,
                Context.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to update last-used metadata for device credential {CredentialId}.", credential.Id);
        }
    }

    private bool RouteHasDeviceId(out int deviceId)
    {
        deviceId = 0;

        var routeValue = Context.Request.RouteValues["deviceId"]?.ToString();
        return int.TryParse(routeValue, out deviceId) && deviceId > 0;
    }

    private static AuthenticateResult FailAuthentication()
    {
        return AuthenticateResult.Fail("Device credential authentication failed.");
    }
}
