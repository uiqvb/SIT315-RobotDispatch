using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

using RobotControllerApi.BoundedContexts.Auth.Constants;
using RobotControllerApi.BoundedContexts.Auth.Handlers;
using RobotControllerApi.BoundedContexts.Auth.Services;

using RobotControllerApi.Infrastructure;
using RobotControllerApi.Infrastructure.DataAccess;
using RobotControllerApi.Infrastructure.DataAccess.ADO;
using RobotControllerApi.Infrastructure.DataAccess.EFCore;
using RobotControllerApi.Infrastructure.DataAccess.Repository;
using RobotControllerApi.Infrastructure.Logging;

using RobotControllerApi.BoundedContexts.CommandCatalogues.Persistence;
using RobotControllerApi.BoundedContexts.CommandCatalogues.Services;

using RobotControllerApi.BoundedContexts.Maps.Persistence;
using RobotControllerApi.BoundedContexts.Maps.Services;

using RobotControllerApi.BoundedContexts.Devices.Persistence;
using RobotControllerApi.BoundedContexts.Devices.Services;

using RobotControllerApi.BoundedContexts.DeviceCapabilities.Persistence;
using RobotControllerApi.BoundedContexts.DeviceCapabilities.Services;

using RobotControllerApi.BoundedContexts.DeviceStatuses.Persistence;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Services;

using RobotControllerApi.BoundedContexts.DeviceCredentials.Persistence;
using RobotControllerApi.BoundedContexts.DeviceCredentials.Services;

using RobotControllerApi.BoundedContexts.AppUsers.Persistence;
using RobotControllerApi.BoundedContexts.AppUsers.Services;

using RobotControllerApi.BoundedContexts.DevicePermissions.Persistence;
using RobotControllerApi.BoundedContexts.DevicePermissions.Services;

using RobotControllerApi.BoundedContexts.Jobs.Persistence;
using RobotControllerApi.BoundedContexts.Jobs.Services;

using RobotControllerApi.BoundedContexts.Workflows.Persistence;
using RobotControllerApi.BoundedContexts.Workflows.Services;

using RobotControllerApi.BoundedContexts.JobHistories.Persistence;
using RobotControllerApi.BoundedContexts.JobHistories.Services;

using RobotControllerApi.BoundedContexts.WorkflowHistories.Persistence;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Services;

using RobotControllerApi.BoundedContexts.Rollbacks.Persistence;
using RobotControllerApi.BoundedContexts.Rollbacks.Services;

using RobotControllerApi.BoundedContexts.Telemetry.Persistence;
using RobotControllerApi.BoundedContexts.Telemetry.Services;
using RobotControllerApi.BoundedContexts.LiveControls.Persistence;
using RobotControllerApi.BoundedContexts.LiveControls.Services;


// Every timestamp column in this schema is `timestamp without time zone` and every value the
// app writes is DateTime.UtcNow. Npgsql 6+ maps a DateTime with Kind=Utc to `timestamptz`, so
// PostgreSQL converted it into the server's local zone on the way in — a UTC+10 machine stored
// 15:25 where 05:25 was meant, ten hours ahead of the DateTime.UtcNow it is later compared to.
//
// The visible damage was lease expiry: LeaseExpiresAtUtc landed ten hours in the future, so
// ExpireStaleWork never fired and a stalled robot's queue could never drain.
//
// Legacy behaviour writes the DateTime verbatim, which is what these columns expect.
// Must be set before the first Npgsql call.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // Swagger does not automatically know about the custom Basic auth handler.
    // This makes the Authorize button appear and causes Try-it-out requests
    // to include: Authorization: Basic <base64(email:password)>
    options.AddSecurityDefinition(AuthenticationSchemes.Basic, new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "basic",
        In = ParameterLocation.Header,
        Description = "Enter app-user credentials, for example Admin@test.com / <REDACTED-PASSWORD>."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = AuthenticationSchemes.Basic
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSpectreRequestLogging(builder.Configuration);

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = AuthenticationSchemes.Basic;
        options.DefaultChallengeScheme = AuthenticationSchemes.Basic;
    })
    .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
        AuthenticationSchemes.Basic,
        options => { })
    .AddScheme<AuthenticationSchemeOptions, DeviceCredentialAuthenticationHandler>(
        AuthenticationSchemes.DeviceCredential,
        options => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.HumanUser, policy =>
        policy.AddAuthenticationSchemes(AuthenticationSchemes.Basic)
              .RequireAuthenticatedUser());

    options.AddPolicy(AuthorizationPolicies.DeviceAdapter, policy =>
        policy.AddAuthenticationSchemes(AuthenticationSchemes.DeviceCredential)
              .RequireAuthenticatedUser());

    options.AddPolicy(AuthorizationPolicies.AdminOnly, policy =>
        policy.AddAuthenticationSchemes(AuthenticationSchemes.Basic)
              .RequireAuthenticatedUser()
              .RequireRole("Admin"));

    // Fail-closed default: any endpoint without an explicit [Authorize]/[AllowAnonymous]
    // is denied unless the caller is an authenticated user (via the default Basic scheme).
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var dbConfig = new DbConfig(builder.Configuration);
builder.Services.AddSingleton(dbConfig);

RegisterServices(builder.Services);
RegisterPersistence(builder.Services, dbConfig);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// app.UseHttpsRedirection();
// Static files (the dashboard) are served before auth so the fail-closed
// fallback policy does not block anonymous access to the SPA.
app.UseDefaultFiles();
app.UseStaticFiles();

// After static files so the dashboard's own assets stay out of the console, and
// before authentication so rejected (401/403) calls are still reported.
app.UseSpectreRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

static void RegisterServices(IServiceCollection services)
{
    services.AddSingleton<MultiPasswordHashService>();
    services.AddSingleton<MultiDeviceCredentialSecretHashService>();
    services.AddScoped<AuthService>();
    services.AddScoped<CurrentUserAccessor>();
    services.AddScoped<DevicePermissionAuthorizationService>();
    services.AddScoped<ICommandCatalogueService, CommandCatalogueService>();
    services.AddScoped<IMapService, MapService>();
    services.AddScoped<IDeviceService, DeviceService>();
    services.AddScoped<IDeviceCapabilityService, DeviceCapabilityService>();
    services.AddScoped<IDeviceStatusService, DeviceStatusService>();
    services.AddScoped<IDeviceCredentialService, DeviceCredentialService>();
    services.AddScoped<IAppUserService, AppUserService>();
    services.AddScoped<IDevicePermissionService, DevicePermissionService>();

    services.AddScoped<IJobService, JobService>();
    services.AddScoped<IWorkDispatchService, WorkDispatchService>();
    services.AddScoped<IWorkflowService, WorkflowService>();
    services.AddScoped<IJobHistoryService, JobHistoryService>();
    services.AddScoped<IWorkflowHistoryService, WorkflowHistoryService>();
    services.AddScoped<IRollbackService, RollbackService>();
    services.AddScoped<IConditionClassifier, ConditionClassifier>();
    services.AddScoped<ITelemetryService, TelemetryService>();
    services.AddScoped<ILiveControlService, LiveControlService>();

    // Drains expired leases without waiting for the stalled robot to poll again.
    services.AddSingleton<PostgresAdvisoryLock>(); //one sweeper across replicas, held by PostgreSQL
    services.AddHostedService<StaleWorkExpiryService>();
}

static void RegisterPersistence(IServiceCollection services, DbConfig dbConfig)
{
    var provider = dbConfig.GetPersistenceProvider().Trim().ToUpperInvariant();

    switch (provider)
    {
        case "EF":
        case "EFCORE":
            RegisterEfCorePersistence(services, dbConfig);
            break;

        case "REPOSITORY":
        case "FASTMEMBER":
            RegisterRepositoryPersistence(services);
            break;

        case "ADO":
        default:
            RegisterAdoPersistence(services);
            break;
    }
}

static void RegisterAdoPersistence(IServiceCollection services)
{
    services.AddScoped<ICommandCatalogueDataAccess, CommandCatalogueADO>();
    services.AddScoped<IMapDataAccess, MapADO>();
    services.AddScoped<IDeviceDataAccess, DeviceADO>();
    services.AddScoped<IDeviceCapabilityDataAccess, DeviceCapabilityADO>();
    services.AddScoped<IDeviceStatusDataAccess, DeviceStatusADO>();
    services.AddScoped<IDeviceCredentialDataAccess, DeviceCredentialADO>();
    services.AddScoped<IAppUserDataAccess, AppUserADO>();
    services.AddScoped<IAppUserCredentialDataAccess, AppUserCredentialADO>();
    services.AddScoped<IDevicePermissionDataAccess, DevicePermissionADO>();

    services.AddScoped<IJobDataAccess, JobADO>();
    services.AddScoped<IWorkflowDataAccess, WorkflowADO>();
    services.AddScoped<IJobHistoryDataAccess, JobHistoryADO>();
    services.AddScoped<IWorkflowHistoryDataAccess, WorkflowHistoryADO>();
    services.AddScoped<IRollbackRequestDataAccess, RollbackRequestADO>();
    services.AddScoped<ITelemetryReadingDataAccess, TelemetryReadingADO>();
    services.AddScoped<ILiveControlCommandDataAccess, LiveControlCommandADO>();
    services.AddScoped<ILiveControlSessionDataAccess, LiveControlSessionADO>();
    services.AddScoped<ILiveControlSegmentDataAccess, LiveControlSegmentADO>();
}

static void RegisterEfCorePersistence(IServiceCollection services, DbConfig dbConfig)
{
    services.AddDbContext<RobotContext>(options =>
    {
        options.UseNpgsql(dbConfig.GetConnectionString());
    });

    services.AddScoped<ICommandCatalogueDataAccess, CommandCatalogueEF>();
    services.AddScoped<IMapDataAccess, MapEF>();
    services.AddScoped<IDeviceDataAccess, DeviceEF>();
    services.AddScoped<IDeviceCapabilityDataAccess, DeviceCapabilityEF>();
    services.AddScoped<IDeviceStatusDataAccess, DeviceStatusEF>();
    services.AddScoped<IDeviceCredentialDataAccess, DeviceCredentialEF>();
    services.AddScoped<IAppUserDataAccess, AppUserEF>();
    services.AddScoped<IAppUserCredentialDataAccess, AppUserCredentialEF>();
    services.AddScoped<IDevicePermissionDataAccess, DevicePermissionEF>();

    services.AddScoped<IJobDataAccess, JobEF>();
    services.AddScoped<IWorkflowDataAccess, WorkflowEF>();
    services.AddScoped<IJobHistoryDataAccess, JobHistoryEF>();
    services.AddScoped<IWorkflowHistoryDataAccess, WorkflowHistoryEF>();
    services.AddScoped<IRollbackRequestDataAccess, RollbackRequestEF>();
    services.AddScoped<ITelemetryReadingDataAccess, TelemetryReadingEF>();
    services.AddScoped<ILiveControlCommandDataAccess, LiveControlCommandEF>();
    services.AddScoped<ILiveControlSessionDataAccess, LiveControlSessionEF>();
    services.AddScoped<ILiveControlSegmentDataAccess, LiveControlSegmentEF>();
}

static void RegisterRepositoryPersistence(IServiceCollection services)
{
    services.AddScoped<ICommandCatalogueDataAccess, CommandCatalogueRepository>();
    services.AddScoped<IMapDataAccess, MapRepository>();
    services.AddScoped<IDeviceDataAccess, DeviceRepository>();
    services.AddScoped<IDeviceCapabilityDataAccess, DeviceCapabilityRepository>();
    services.AddScoped<IDeviceStatusDataAccess, DeviceStatusRepository>();
    services.AddScoped<IDeviceCredentialDataAccess, DeviceCredentialRepository>();
    services.AddScoped<IAppUserDataAccess, AppUserRepository>();
    services.AddScoped<IAppUserCredentialDataAccess, AppUserCredentialRepository>();
    services.AddScoped<IDevicePermissionDataAccess, DevicePermissionRepository>();

    services.AddScoped<IJobDataAccess, JobRepository>();
    services.AddScoped<IWorkflowDataAccess, WorkflowRepository>();
    services.AddScoped<IJobHistoryDataAccess, JobHistoryRepository>();
    services.AddScoped<IWorkflowHistoryDataAccess, WorkflowHistoryRepository>();
    services.AddScoped<IRollbackRequestDataAccess, RollbackRequestRepository>();
    services.AddScoped<ITelemetryReadingDataAccess, TelemetryReadingRepository>();
    services.AddScoped<ILiveControlCommandDataAccess, LiveControlCommandRepository>();
    services.AddScoped<ILiveControlSessionDataAccess, LiveControlSessionRepository>();
    services.AddScoped<ILiveControlSegmentDataAccess, LiveControlSegmentRepository>();
}