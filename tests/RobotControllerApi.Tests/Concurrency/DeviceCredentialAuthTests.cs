using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RobotControllerApi.BoundedContexts.Auth.Constants;
using RobotControllerApi.BoundedContexts.Auth.Handlers;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Models;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Services;
using RobotControllerApi.Infrastructure.DataAccess.ADO;

namespace RobotControllerApi.Tests.Concurrency;

// Phase 1 F3/F4: the handler against real PostgreSQL, with a data access wrapper that fails the test on a full-table load.
[Collection("postgres")]
public class DeviceCredentialAuthTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;

    public DeviceCredentialAuthTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync() => _db.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(AuthenticateResult Result, NoFullScanCredentials Credentials)> AuthenticateAsync(string identifier, string secret, int? routeDeviceId = null)
    {
        var credentials = new NoFullScanCredentials(new DeviceCredentialADO(_db.DbConfig));
        var handler = new DeviceCredentialAuthenticationHandler(
            new StaticOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default,
            credentials, new DeviceADO(_db.DbConfig), new MultiDeviceCredentialSecretHashService(_db.Configuration), _db.Configuration);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Device-Credential-Id"] = identifier;
        context.Request.Headers["X-Device-Credential-Secret"] = secret;
        if (routeDeviceId.HasValue) context.Request.RouteValues["deviceId"] = routeDeviceId.Value.ToString();

        await handler.InitializeAsync(new AuthenticationScheme("DeviceCredential", null, typeof(DeviceCredentialAuthenticationHandler)), context);
        return (await handler.AuthenticateAsync(), credentials);
    }

    [Fact]
    public async Task ValidCredential_Authenticates_WithDeviceAndCredentialClaims_WithoutLoadingEveryCredential()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        await _db.InsertCredentialAsync(PostgresFixture.LegacyDeviceId, "legacy-b", "secret-b");

        var (result, _) = await AuthenticateAsync("NANO-A", "secret-a");

        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst(AuthClaimTypes.DeviceId)!.Value.Should().Be(PostgresFixture.NanoDeviceId.ToString());
        result.Principal!.FindFirst(AuthClaimTypes.DeviceCredentialId)!.Value.Should().Be(credentialId.ToString());
    }

    [Fact]
    public async Task WrongSecret_IsRejected()
    {
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        (await AuthenticateAsync("nano-a", "not-the-secret")).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownIdentifier_IsRejected()
    {
        (await AuthenticateAsync("nobody", "secret-a")).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task InactiveRevokedOrExpiredCredentials_AreRejected()
    {
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "inactive", "s", isActive: false);
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "revoked", "s", revokedAtUtc: DateTime.UtcNow.AddMinutes(-1));
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "expired", "s", expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));

        (await AuthenticateAsync("inactive", "s")).Result.Succeeded.Should().BeFalse();
        (await AuthenticateAsync("revoked", "s")).Result.Succeeded.Should().BeFalse();
        (await AuthenticateAsync("expired", "s")).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task RouteDeviceThatIsNotTheCredentialsDevice_IsRejected()
    {
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        (await AuthenticateAsync("nano-a", "secret-a", routeDeviceId: PostgresFixture.LegacyDeviceId)).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task LastUsed_IsWrittenOnFirstUse_ThenSkippedInsideTheInterval()
    {
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");

        var first = await AuthenticateAsync("nano-a", "secret-a");
        var second = await AuthenticateAsync("nano-a", "secret-a");

        first.Credentials.LastUsedWrites.Should().Be(1);
        second.Credentials.LastUsedWrites.Should().Be(0);
        (await _db.ScalarAsync<object>("SELECT lastusedatutc FROM public.devicecredential WHERE credentialidentifier = 'nano-a';")).Should().NotBe(DBNull.Value);
    }

    [Fact]
    public async Task LastUsed_IsWrittenAgain_OnceTheIntervalHasPassed()
    {
        await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a", lastUsedAtUtc: DateTime.UtcNow.AddMinutes(-11));

        (await AuthenticateAsync("nano-a", "secret-a")).Credentials.LastUsedWrites.Should().Be(1);
    }

    [Fact]
    public async Task LastUsedWrite_DoesNotUndoARevocationThatHappenedAfterTheCredentialWasRead()
    {
        var credentialId = await _db.InsertCredentialAsync(PostgresFixture.NanoDeviceId, "nano-a", "secret-a");
        await _db.ExecuteAsync("UPDATE public.devicecredential SET revokedatutc = @at WHERE id = @id;", ("at", DateTime.UtcNow), ("id", credentialId));

        await new DeviceCredentialADO(_db.DbConfig).UpdateDeviceCredentialLastUsedAsync(credentialId, DateTime.UtcNow, "127.0.0.1", "test");

        (await _db.ScalarAsync<object>("SELECT revokedatutc FROM public.devicecredential WHERE id = @id;", ("id", credentialId))).Should().NotBe(DBNull.Value);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class NoFullScanCredentials : IDeviceCredentialDataAccess
    {
        private readonly IDeviceCredentialDataAccess _inner;
        public int LastUsedWrites { get; private set; }

        public NoFullScanCredentials(IDeviceCredentialDataAccess inner) => _inner = inner;

        public List<DeviceCredential> GetDeviceCredentials() => throw new InvalidOperationException("Authentication must not load every credential.");
        public Task<DeviceCredential?> GetDeviceCredentialByCredentialIdentifierAsync(string credentialIdentifier, CancellationToken ct = default) => _inner.GetDeviceCredentialByCredentialIdentifierAsync(credentialIdentifier, ct);

        public Task<bool> UpdateDeviceCredentialLastUsedAsync(int id, DateTime lastUsedAtUtc, string? lastUsedIpAddress, string? lastUsedUserAgent, CancellationToken ct = default)
        {
            LastUsedWrites++;
            return _inner.UpdateDeviceCredentialLastUsedAsync(id, lastUsedAtUtc, lastUsedIpAddress, lastUsedUserAgent, ct);
        }

        public DeviceCredential? GetDeviceCredentialById(int id) => _inner.GetDeviceCredentialById(id);
        public List<DeviceCredential> GetDeviceCredentialsByDeviceId(int deviceId) => _inner.GetDeviceCredentialsByDeviceId(deviceId);
        public bool DeviceCredentialExistsByCredentialIdentifier(string credentialIdentifier, int? excludeId = null) => _inner.DeviceCredentialExistsByCredentialIdentifier(credentialIdentifier, excludeId);
        public DeviceCredential InsertDeviceCredential(DeviceCredential newDeviceCredential) => _inner.InsertDeviceCredential(newDeviceCredential);
        public bool UpdateDeviceCredential(int id, DeviceCredential updatedDeviceCredential) => throw new InvalidOperationException("Authentication must not write the whole credential row.");
        public bool DeleteDeviceCredential(int id) => _inner.DeleteDeviceCredential(id);
    }
}
