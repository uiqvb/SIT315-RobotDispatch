using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RobotControllerApi.BoundedContexts.Auth.Constants;
using RobotControllerApi.BoundedContexts.Auth.Services;
using RobotControllerApi.BoundedContexts.Jobs.Dtos;
using RobotControllerApi.BoundedContexts.Jobs.Services;

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
    public ActionResult ClaimNext(int deviceId, ClaimJobRequest request)
    {
        try
        {
            var deviceCredentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User); //gets the credientialID, that means the ID of the crediential that exists. Crediential=entire record around the password/secret
            var authenticatedDeviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var response = _service.ClaimNextWorkItem(deviceId, request, deviceCredentialId, authenticatedDeviceId);
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
    public ActionResult ReportOfflineRollback(int deviceId, ReportOfflineRollbackRequest request)
    {
        try
        {
            var deviceCredentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var authenticatedDeviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            return Ok(_service.ReportOfflineRollback(deviceId, request, deviceCredentialId, authenticatedDeviceId));
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}
