using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Services;
using RobotControllerApi.Infrastructure.DataAccess;
using Testcontainers.PostgreSql;

namespace RobotControllerApi.Tests.Concurrency;

internal static class NpgsqlTimestampMode
{
    // Same switch Program.cs sets, applied before any test touches Npgsql.
    [ModuleInitializer]
    internal static void Enable() => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
}

// A throwaway PostgreSQL loaded with the real schema, so guarded SQL is tested against the database that enforces it.
public sealed class PostgresFixture : IAsyncLifetime
{
    public const int NanoDeviceId = 1;
    public const int LegacyDeviceId = 2;
    public const int StopCommandId = 2;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    public IConfiguration Configuration { get; private set; } = null!;
    public DbConfig DbConfig { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _container.GetConnectionString(),
                ["DeviceCredential:HashKey"] = "integration-test-hash-key",
                ["WorkDispatch:LeaseMinutes"] = "5",
                ["DeviceAuth:LastUsedPersistIntervalMinutes"] = "10"
            })
            .Build();

        DbConfig = new DbConfig(Configuration);

        var databaseFolder = FindDatabaseFolder();
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(databaseFolder, "Final_project_schema_refactored.sql")));
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(databaseFolder, "Final_project_reference_data_refactored.sql")));
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task ResetAsync() => ExecuteAsync(
        "TRUNCATE public.jobhistory, public.workflowhistory, public.job, public.workflow, public.devicecredential RESTART IDENTITY CASCADE;");

    public async Task<int> InsertCredentialAsync(int deviceId, string identifier, string rawSecret,
        bool isActive = true, DateTime? revokedAtUtc = null, DateTime? expiresAtUtc = null, DateTime? lastUsedAtUtc = null)
    {
        var hash = new MultiDeviceCredentialSecretHashService(Configuration).HashSecret(rawSecret);

        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.devicecredential (deviceid, name, credentialidentifier, secretkeyprefix, secrethash, hashalgorithm, isactive, revokedatutc, expiresatutc, lastusedatutc)
              VALUES (@deviceId, @name, @identifier, @prefix, @hash, 'HMACSHA256_V1', @isActive, @revokedAtUtc, @expiresAtUtc, @lastUsedAtUtc)
              RETURNING id;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("name", identifier);
        cmd.Parameters.AddWithValue("identifier", identifier);
        cmd.Parameters.AddWithValue("prefix", rawSecret[..Math.Min(16, rawSecret.Length)]);
        cmd.Parameters.AddWithValue("hash", hash);
        cmd.Parameters.AddWithValue("isActive", isActive);
        cmd.Parameters.AddWithValue("revokedAtUtc", (object?)revokedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("expiresAtUtc", (object?)expiresAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lastUsedAtUtc", (object?)lastUsedAtUtc ?? DBNull.Value);

        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<int> InsertQueuedJobAsync(int deviceId, int commandCatalogueId = StopCommandId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO public.job (deviceid, commandcatalogueid, payloadjson, providertype, status)
              VALUES (@deviceId, @commandCatalogueId, '{}'::jsonb, 'Api', 'Queued')
              RETURNING id;", conn);

        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("commandCatalogueId", commandCatalogueId);

        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    // Pushes the lease into the past, which is what a robot going silent for longer than the lease looks like.
    public async Task ExpireLeaseAsync(int jobId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE public.job SET leaseexpiresatutc = @past WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("past", DateTime.UtcNow.AddMinutes(-1));
        cmd.Parameters.AddWithValue("id", jobId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(DbConfig.GetConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    private static string FindDatabaseFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "Infrastructure", "DataAccess", "Database");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find Infrastructure/DataAccess/Database above the test output folder.");
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
