using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RobotControllerApi.BoundedContexts.Auth.Constants;
using RobotControllerApi.BoundedContexts.Auth.Services;
using RobotControllerApi.BoundedContexts.Jobs.Dtos;
using RobotControllerApi.BoundedContexts.Jobs.Services;
using RobotControllerApi.Infrastructure;

namespace RobotControllerApi.BoundedContexts.Jobs.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.DeviceAdapter)]
[Route("api/adapter/devices/{deviceId}/work-items")]
public class WorkDispatchController : ControllerBase
{
    private readonly IWorkDispatchService _service;
    private readonly CurrentUserAccessor _currentUserAccessor;

    public WorkDispatchController(IWorkDispatchService service, CurrentUserAccessor currentUserAccessor)
    {
        _service = service;
        _currentUserAccessor = currentUserAccessor;
    }

    [HttpPost("claim-next")]
    [EnableRateLimiting(DispatchBackpressure.PolicyName)] //shares the dispatch permit pool
    public async Task<ActionResult> ClaimNext(int deviceId, ClaimJobRequest request, CancellationToken ct)
    {
        try
        {
            var deviceCredentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User); //gets the credientialID, that means the ID of the crediential that exists. Crediential=entire record around the password/secret
            var authenticatedDeviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var response = await _service.ClaimNextWorkItemAsync(deviceId, request, deviceCredentialId, authenticatedDeviceId, ct); //request thread is released while PostgreSQL works
            if (response == null) return NoContent();
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    // Called by a robot on reconnect, after it rolled itself back while the backend was
    // unreachable. Until this lands the backend's pose is stale by however far the robot drove.
    [HttpPost("report-offline-rollback")]
    public async Task<ActionResult> ReportOfflineRollback(int deviceId, ReportOfflineRollbackRequest request, CancellationToken ct)
    {
        try
        {
            var deviceCredentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var authenticatedDeviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            return Ok(await _service.ReportOfflineRollbackAsync(deviceId, request, deviceCredentialId, authenticatedDeviceId, ct));
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}
