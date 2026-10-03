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
    public async Task<ActionResult> CancelJob(int id, CancellationToken ct)
    {
        try
        {
            var success = await _service.CancelJobAsync(id, ct);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.HumanUser)]
    [HttpPatch("{id}/deactivate")]
    public Task<ActionResult> DeactivateJob(int id, CancellationToken ct) => CancelJob(id, ct);

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
    [EnableRateLimiting(DispatchBackpressure.PolicyName)] //shares the dispatch permit pool
    public async Task<ActionResult> MarkJobStarted(int jobId, StartJobRequest request, CancellationToken ct)
    {
        try
        {
            var credentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var deviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var success = await _service.MarkJobStartedAsync(jobId, request, credentialId, deviceId, ct); //request carries the claimedAtUtc the robot echoed back
            if (!success) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.DeviceAdapter)]
    [HttpPatch("/api/adapter/jobs/{jobId}/completed")]
    [EnableRateLimiting(DispatchBackpressure.PolicyName)] //shares the dispatch permit pool
    public async Task<ActionResult> MarkJobCompleted(int jobId, CompleteJobRequest request, CancellationToken ct)
    {
        try
        {
            var credentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var deviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var success = await _service.MarkJobCompletedAsync(jobId, request, credentialId, deviceId, ct);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    [Authorize(Policy = AuthorizationPolicies.DeviceAdapter)]
    [HttpPatch("/api/adapter/jobs/{jobId}/failed")]
    [EnableRateLimiting(DispatchBackpressure.PolicyName)] //shares the dispatch permit pool
    public async Task<ActionResult> MarkJobFailed(int jobId, FailJobRequest request, CancellationToken ct)
    {
        try
        {
            var credentialId = _currentUserAccessor.GetRequiredDeviceCredentialId(User);
            var deviceId = _currentUserAccessor.GetRequiredDeviceId(User); //device the credential authenticated as, from the auth claims
            var success = await _service.MarkJobFailedAsync(jobId, request, credentialId, deviceId, ct);
            if (!success) return NotFound();
            return NoContent();
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }
}
