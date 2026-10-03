using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RobotControllerApi.BoundedContexts.Auth.Constants;
using RobotControllerApi.BoundedContexts.Auth.Services;
using RobotControllerApi.BoundedContexts.Jobs.Dtos;
using RobotControllerApi.BoundedContexts.Jobs.Services;

namespace RobotControllerApi.BoundedContexts.Jobs.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IJobService _service;
    private readonly CurrentUserAccessor _currentUserAccessor;

    public JobsController(IJobService service, CurrentUserAccessor currentUserAccessor)
    {
        _service = service;
        _currentUserAccessor = currentUserAccessor;
    }

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpGet]
    public ActionResult GetJobs() => Ok(_service.GetJobs());

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpGet("{id}")]
    public ActionResult GetJobById(int id)
    {
        var response = _service.GetJobById(id);
        if (response == null) return NotFound();
        return Ok(response);
    }

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpGet("/api/devices/{deviceId}/jobs")]
    public ActionResult GetJobsByDeviceId(int deviceId) => Ok(_service.GetJobsByDeviceId(deviceId));

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpPost("/api/devices/{deviceId}/jobs")]
    public ActionResult CreateJob(int deviceId, CreateJobRequest request)
    {
        try
        {
            var appUserId = _currentUserAccessor.GetRequiredAppUserId(User);
            var response = _service.CreateJob(deviceId, request, appUserId);
            return CreatedAtAction(nameof(GetJobById), new { id = response.Id }, response);
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpPut("{id}")]
    public ActionResult UpdateJob(int id, UpdateJobRequest request)
    {
        try
        {
            var success = _service.UpdateJob(id, request);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpPatch("{id}/cancel")]
    public ActionResult CancelJob(int id)
    {
        try
        {
            var success = _service.CancelJob(id);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpPatch("{id}/deactivate")]
    public ActionResult DeactivateJob(int id) => CancelJob(id);

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpDelete("{id}")]
    public ActionResult DeleteJob(int id)
    {
        try
        {
            var success = _service.DeleteJob(id);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.DeviceAdapter)]
    [HttpPatch("/api/adapter/jobs/{jobId}/started")]
    public ActionResult MarkJobStarted(int jobId, StartJobRequest request)
    {
        try
        {
            var credentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var deviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var success = _service.MarkJobStarted(jobId, request, credentialId, deviceId); //request carries the claimedAtUtc the robot echoed back
            if (!success) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.DeviceAdapter)]
    [HttpPatch("/api/adapter/jobs/{jobId}/completed")]
    public ActionResult MarkJobCompleted(int jobId, CompleteJobRequest request)
    {
        try
        {
            var credentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var deviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var success = _service.MarkJobCompleted(jobId, request, credentialId, deviceId);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.DeviceAdapter)]
    [HttpPatch("/api/adapter/jobs/{jobId}/failed")]
    public ActionResult MarkJobFailed(int jobId, FailJobRequest request)
    {
        try
        {
            var credentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var deviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var success = _service.MarkJobFailed(jobId, request, credentialId, deviceId);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}
