using System.Text.Json;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Persistence;
using RobotControllerApi.BoundedContexts.JobHistories.Models;
using RobotControllerApi.BoundedContexts.JobHistories.Persistence;
using RobotControllerApi.BoundedContexts.Jobs.Dtos;
using RobotControllerApi.BoundedContexts.Jobs.Models;
using RobotControllerApi.BoundedContexts.Jobs.Persistence;
using RobotControllerApi.BoundedContexts.Maps.Models;
using RobotControllerApi.BoundedContexts.Maps.Persistence;
using RobotControllerApi.BoundedContexts.Shared;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Models;
using RobotControllerApi.BoundedContexts.WorkflowHistories.Persistence;
using RobotControllerApi.BoundedContexts.Workflows.Models;
using RobotControllerApi.BoundedContexts.Workflows.Persistence;

namespace RobotControllerApi.BoundedContexts.Jobs.Services;

public class JobService : IJobService
{
    private readonly IJobDataAccess _dataAccess;
    private readonly IWorkflowDataAccess _workflowDataAccess;
    private readonly IJobHistoryDataAccess _jobHistoryDataAccess;
    private readonly IWorkflowHistoryDataAccess _workflowHistoryDataAccess;
    private readonly IDeviceStatusDataAccess _deviceStatusDataAccess;
    private readonly IMapDataAccess _mapDataAccess;
    private readonly IConfiguration _configuration;

    public JobService(
        IJobDataAccess dataAccess,
        IWorkflowDataAccess workflowDataAccess,
        IJobHistoryDataAccess jobHistoryDataAccess,
        IWorkflowHistoryDataAccess workflowHistoryDataAccess,
        IDeviceStatusDataAccess deviceStatusDataAccess,
        IMapDataAccess mapDataAccess,
        IConfiguration configuration)
    {
        _dataAccess = dataAccess;
        _workflowDataAccess = workflowDataAccess;
        _jobHistoryDataAccess = jobHistoryDataAccess;
        _workflowHistoryDataAccess = workflowHistoryDataAccess;
        _deviceStatusDataAccess = deviceStatusDataAccess;
        _mapDataAccess = mapDataAccess;
        _configuration = configuration;
    }

    public List<JobResponse> GetJobs() => _dataAccess.GetJobs().Select(MapToResponse).ToList();
    public JobResponse? GetJobById(int id) => _dataAccess.GetJobById(id) is { } model ? MapToResponse(model) : null;
    public List<JobResponse> GetJobsByDeviceId(int deviceId) => _dataAccess.GetJobsByDeviceId(deviceId).Select(MapToResponse).ToList();
    public List<JobResponse> GetJobsByWorkflowId(int workflowId) => _dataAccess.GetJobsByWorkflowId(workflowId).Select(MapToResponse).ToList();

    public JobResponse CreateJob(int deviceId, CreateJobRequest request, int? requestedByAppUserId = null) //appuserid is passed through here
    {
        ValidateJob(deviceId, request.CommandCatalogueId, request.IsRollback ? "Rollback" : request.ProviderType, "Queued", request.PayloadJson, request.WorkflowId, request.StepNumber, request.IsRollback);

        var commandName = _dataAccess.GetCommandCatalogueNameById(request.CommandCatalogueId) ?? string.Empty;
        if (!commandName.Equals("STOP", StringComparison.OrdinalIgnoreCase))
        {
            EnsureDeviceQueueHasCapacity(deviceId);
        }

        var now = DateTime.UtcNow;
        var model = new Job
        {
            DeviceId = deviceId,
            WorkflowId = request.WorkflowId,
            StepNumber = request.StepNumber,
            CommandCatalogueId = request.CommandCatalogueId,
            PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson,
            ProviderType = request.IsRollback ? "Rollback" : request.ProviderType,
            Status = "Queued",
            RequestedByAppUserId = requestedByAppUserId ?? request.RequestedByAppUserId, //it is created as part of the job.
            IsRollback = request.IsRollback,
            RollbackOfJobHistoryId = request.RollbackOfJobHistoryId,
            CreatedDate = now,
            ModifiedDate = now
        };
        return MapToResponse(_dataAccess.InsertJob(model));
    }

    public bool UpdateJob(int id, UpdateJobRequest request)
    {
        var existing = _dataAccess.GetJobById(id);
        if (existing == null) return false;
        ValidateJob(request.DeviceId, request.CommandCatalogueId, request.IsRollback ? "Rollback" : request.ProviderType, request.Status, request.PayloadJson, request.WorkflowId, request.StepNumber, request.IsRollback);
        existing.DeviceId = request.DeviceId;
        existing.WorkflowId = request.WorkflowId;
        existing.StepNumber = request.StepNumber;
        existing.CommandCatalogueId = request.CommandCatalogueId;
        existing.PayloadJson = string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson;
        existing.ProviderType = request.IsRollback ? "Rollback" : request.ProviderType;
        existing.Status = request.Status;
        existing.RequestedByAppUserId = request.RequestedByAppUserId;
        existing.ClaimedByDeviceCredentialId = request.ClaimedByDeviceCredentialId;
        existing.ClaimedAtUtc = request.ClaimedAtUtc;
        existing.LeaseExpiresAtUtc = request.LeaseExpiresAtUtc;
        existing.IsRollback = request.IsRollback;
        existing.RollbackOfJobHistoryId = request.RollbackOfJobHistoryId;
        existing.ModifiedDate = DateTime.UtcNow;
        return _dataAccess.UpdateJob(existing.Id, existing);
    }

    public bool DeleteJob(int id)
    {
        if (_dataAccess.GetJobById(id) == null) return false;
        return _dataAccess.DeleteJob(id);
    }

    public async Task<bool> CancelJobAsync(int id, CancellationToken ct = default)
    {
        var existing = await _dataAccess.GetJobByIdAsync(id, ct);
        if (existing == null) return false;
        if (existing.Status is "Completed" or "Failed" or "RolledBack") throw new InvalidOperationException("Completed, failed, or rolled-back jobs cannot be cancelled.");
        existing.Status = "Cancelled";
        existing.ModifiedDate = DateTime.UtcNow;
        var updated = await _dataAccess.UpdateJobAsync(existing.Id, existing, ct);
        if (updated && existing.WorkflowId.HasValue) await FinalizeParentWorkflowIfReadyAsync(existing.WorkflowId.Value, DateTime.UtcNow, ct);
        return updated;
    }

    public Task<bool> DeactivateJobAsync(int id, CancellationToken ct = default) => CancelJobAsync(id, ct);

    public async Task<bool> MarkJobStartedAsync(int id, StartJobRequest request, int deviceCredentialId, int deviceId, CancellationToken ct = default)
    {
        var claimedAtUtc = RequireClaimedAtUtc(request.ClaimedAtUtc); //the claim id the robot got from claim-next and echoed back
        var existing = await _dataAccess.GetJobByIdAsync(id, ct); //read only to check ownership, never written back
        if (existing == null) return false;
        ValidateAuthenticatedDeviceCanAccessJob(existing, deviceCredentialId, deviceId); //job must belong to the robot's own device

        var now = DateTime.UtcNow;
        var started = await _dataAccess.TryMarkClaimedJobExecutingAsync(id, deviceCredentialId, claimedAtUtc, now, ct); //Claimed -> Executing in one guarded UPDATE
        if (started == null) throw new InvalidOperationException("This job cannot be started from its current status."); //row moved on or wrong claim -> 409

        await MarkParentWorkflowExecutingAsync(started.WorkflowId, deviceCredentialId, now, ct); //parent workflow follows the job into Executing
        return true;
    }

    public async Task<bool> MarkJobCompletedAsync(int id, CompleteJobRequest request, int deviceCredentialId, int deviceId, CancellationToken ct = default)
    {
        var claimedAtUtc = RequireClaimedAtUtc(request.ClaimedAtUtc); //the claim id the robot got from claim-next and echoed back
        var existing = await _dataAccess.GetJobByIdAsync(id, ct); //read only to check ownership, never written back
        if (existing == null) return false;
        ValidateAuthenticatedDeviceCanAccessJob(existing, deviceCredentialId, deviceId); //job must belong to the robot's own device

        var resultJson = string.IsNullOrWhiteSpace(request.ResultJson) ? "{}" : request.ResultJson!;
        ValidateJsonObject(resultJson, "ResultJson");

        var completed = await _dataAccess.TryFinishClaimedJobAsync(id, deviceCredentialId, claimedAtUtc, CompletableStatuses, "Completed", DateTime.UtcNow, ct); //Executing -> Completed in one guarded UPDATE
        if (completed == null) throw new InvalidOperationException("This job cannot be completed from its current status."); //row moved on or wrong claim -> 409

        await InsertHistoryAsync(completed, true, true, resultJson, null, null, ct); //side effects use the row PostgreSQL returned, not the earlier read
        await ApplyCompletedJobPoseAsync(completed, ct);

        // A standalone rollback job has no workflow to finalize, so this is the only moment
        // its original can be marked reversed.
        if (completed.IsRollback && !completed.WorkflowId.HasValue)
        {
            await MarkOriginalsRolledBackAsync(new[] { completed }, completed.ModifiedDate, ct);
        }

        if (completed.WorkflowId.HasValue) await FinalizeParentWorkflowIfReadyAsync(completed.WorkflowId.Value, DateTime.UtcNow, ct);
        return true;
    }

    public async Task<bool> MarkJobFailedAsync(int id, FailJobRequest request, int deviceCredentialId, int deviceId, CancellationToken ct = default)
    {
        var claimedAtUtc = RequireClaimedAtUtc(request.ClaimedAtUtc); //the claim id the robot got from claim-next and echoed back
        var existing = await _dataAccess.GetJobByIdAsync(id, ct); //read only to check ownership, never written back
        if (existing == null) return false;
        ValidateAuthenticatedDeviceCanAccessJob(existing, deviceCredentialId, deviceId); //job must belong to the robot's own device

        existing = await _dataAccess.TryFinishClaimedJobAsync(id, deviceCredentialId, claimedAtUtc, FailableStatuses, "Failed", DateTime.UtcNow, ct); //Claimed/Executing -> Failed in one guarded UPDATE, returns the new row
        if (existing == null) throw new InvalidOperationException("This job cannot be failed from its current status."); //row moved on or wrong claim -> 409

        await InsertHistoryAsync(
            existing,
            false,
            true,
            null,
            string.IsNullOrWhiteSpace(request.FailureCode) ? "JOB_FAILED" : request.FailureCode!.Trim(),
            string.IsNullOrWhiteSpace(request.FailureMessage) ? "Job failed." : request.FailureMessage!.Trim(), ct);

        await InvalidatePoseAfterFailedMovementAsync(existing, ct);

        if (existing.WorkflowId.HasValue)
        {
            var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(existing.WorkflowId.Value, ct);
            if (workflow != null && workflow.ExecutionMode.Equals("AllOrNothing", StringComparison.OrdinalIgnoreCase))
            {
                await CancelQueuedWorkflowJobsAsync(workflow.Id, existing.ModifiedDate, ct);
            }

            await FinalizeParentWorkflowIfReadyAsync(existing.WorkflowId.Value, DateTime.UtcNow, ct);
        }

        return true;
    }

    private void EnsureDeviceQueueHasCapacity(int deviceId)
    {
        var maxQueuedItems = int.TryParse(_configuration["WorkDispatch:MaxQueuedWorkItemsPerDevice"], out var parsed) ? parsed : 3;

        var queuedStandaloneJobs = _dataAccess.GetJobsByDeviceId(deviceId)
            .Count(x => x.WorkflowId == null && x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));

        var queuedWorkflows = _workflowDataAccess.GetWorkflowsByDeviceId(deviceId)
            .Count(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));

        if (queuedStandaloneJobs + queuedWorkflows >= maxQueuedItems)
        {
            throw new InvalidOperationException($"Device {deviceId} already has {maxQueuedItems} queued work item(s). Wait for queued work to be claimed or cancel stale work before queueing more.");
        }
    }

    private async Task MarkParentWorkflowExecutingAsync(int? workflowId, int? deviceCredentialId, DateTime now, CancellationToken ct)
    {
        if (!workflowId.HasValue) return;
        var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(workflowId.Value, ct);
        if (workflow == null || IsTerminalStatus(workflow.Status)) return;
        if (deviceCredentialId.HasValue && workflow.ClaimedByDeviceCredentialId == null) workflow.ClaimedByDeviceCredentialId = deviceCredentialId;
        workflow.Status = "Executing";
        workflow.ModifiedDate = now;
        await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
    }

    private async Task FinalizeParentWorkflowIfReadyAsync(int workflowId, DateTime now, CancellationToken ct)
    {
        var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(workflowId, ct);
        if (workflow == null || IsTerminalStatus(workflow.Status)) return;

        var jobs = await _dataAccess.GetJobsByWorkflowIdAsync(workflowId, ct);

        if (workflow.ExecutionMode.Equals("AllOrNothing", StringComparison.OrdinalIgnoreCase) && jobs.Any(x => x.Status is "Failed" or "Expired"))
        {
            await CancelQueuedWorkflowJobsAsync(workflowId, now, ct);
            jobs = await _dataAccess.GetJobsByWorkflowIdAsync(workflowId, ct);
        }

        if (jobs.Any(x => !IsTerminalStatus(x.Status))) return;

        var success = workflow.ExecutionMode.Equals("BestEffort", StringComparison.OrdinalIgnoreCase)
            ? jobs.Any(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            : jobs.All(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase));

        workflow.Status = success ? "Completed" : "Failed";
        workflow.ModifiedDate = now;
        await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
        await InsertWorkflowHistoryIfMissingAsync(workflow, success, jobs.FirstOrDefault(x => !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))?.StepNumber, success ? null : "Workflow finished with failed steps.", now, ct);

        // Only a rollback that actually finished reverses anything. A failed one leaves the
        // original standing, which is the honest record.
        if (workflow.IsRollback && success)
        {
            await MarkOriginalsRolledBackAsync(jobs, now, ct);
        }
    }

    // Marks the work a completed rollback has undone, so the original stops reading as though
    // it still stands. Without this the RolledBack status was never written by anything: the
    // original workflow sat at Completed forever, and nothing recorded that it had been reversed.
    //
    // The link is each rollback job's RollbackOfJobHistoryId, which points at the history row of
    // the step it reverses. That row carries both the original job and its workflow, so one
    // lookup covers jobs and workflows on both rollback paths.
    private async Task MarkOriginalsRolledBackAsync(IEnumerable<Job> rollbackJobs, DateTime now, CancellationToken ct)
    {
        var historyIds = rollbackJobs
            .Where(x => x.IsRollback && x.RollbackOfJobHistoryId.HasValue)
            .Select(x => x.RollbackOfJobHistoryId!.Value)
            .Distinct()
            .ToList();

        if (historyIds.Count == 0) return;

        var originalJobIds = new HashSet<int>();
        var originalWorkflowIds = new HashSet<int>();

        foreach (var historyId in historyIds)
        {
            var history = await _jobHistoryDataAccess.GetJobHistoryByIdAsync(historyId, ct);
            if (history == null) continue;
            if (history.JobId.HasValue) originalJobIds.Add(history.JobId.Value);
            if (history.WorkflowId.HasValue) originalWorkflowIds.Add(history.WorkflowId.Value);
        }

        foreach (var jobId in originalJobIds)
        {
            var job = await _dataAccess.GetJobByIdAsync(jobId, ct);

            // Only completed work can have been undone. A step that failed or was cancelled
            // never happened, so there is nothing to reverse and its status stays as it is.
            if (job == null || job.IsRollback) continue;
            if (!job.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) continue;

            job.Status = "RolledBack";
            job.ModifiedDate = now;
            await _dataAccess.UpdateJobAsync(job.Id, job, ct);
        }

        foreach (var workflowId in originalWorkflowIds)
        {
            var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(workflowId, ct);
            if (workflow == null || workflow.IsRollback) continue;
            if (!workflow.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) continue;

            workflow.Status = "RolledBack";
            workflow.ModifiedDate = now;
            await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
        }
    }

    private async Task CancelQueuedWorkflowJobsAsync(int workflowId, DateTime now, CancellationToken ct)
    {
        foreach (var queuedJob in (await _dataAccess.GetJobsByWorkflowIdAsync(workflowId, ct)).Where(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)))
        {
            await _dataAccess.TryUpdateQueuedJobStatusAsync(queuedJob.Id, "Cancelled", now, ct); //Queued -> Cancelled, no-op if something claimed it meanwhile
        }
    }

    private async Task InsertWorkflowHistoryIfMissingAsync(Workflow workflow, bool success, int? failedStepNumber, string? failureMessage, DateTime now, CancellationToken ct)
    {
        if ((await _workflowHistoryDataAccess.GetWorkflowHistoriesByWorkflowIdAsync(workflow.Id, ct)).Any()) return;

        await _workflowHistoryDataAccess.InsertWorkflowHistoryAsync(new WorkflowHistory
        {
            WorkflowId = workflow.Id,
            DeviceId = workflow.DeviceId,
            ProviderType = workflow.ProviderType,
            Status = success ? "Completed" : "Failed",
            Executed = (await _dataAccess.GetJobsByWorkflowIdAsync(workflow.Id, ct)).Any(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            Success = success,
            StartedAtUtc = workflow.ClaimedAtUtc,
            CompletedAtUtc = now,
            FailedStepNumber = failedStepNumber,
            FailureMessage = failureMessage,
            RollbackOfWorkflowHistoryId = workflow.RollbackOfWorkflowHistoryId,
            CreatedDate = now
        }, ct);
    }

    private async Task InsertHistoryAsync(Job job, bool success, bool executed, string? resultJson, string? failureCode, string? failureMessage, CancellationToken ct)
    {
        var command = await _dataAccess.GetCommandCatalogueByIdAsync(job.CommandCatalogueId, ct);
        var commandName = command?.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandName)) commandName = $"CommandCatalogue:{job.CommandCatalogueId}";

        var completedAtUtc = DateTime.UtcNow;
        var startedAtUtc = executed ? job.ClaimedAtUtc : null;
        var payloadJson = string.IsNullOrWhiteSpace(job.PayloadJson) ? "{}" : job.PayloadJson;
        var durationMs = executed
            ? TryGetDurationMs(payloadJson) ?? TryGetDurationMs(resultJson) ?? CalculateDurationMs(startedAtUtc, completedAtUtc)
            : null;

        await _jobHistoryDataAccess.InsertJobHistoryAsync(new JobHistory
        {
            JobId = job.Id,
            WorkflowId = job.WorkflowId,
            StepNumber = job.StepNumber,
            DeviceId = job.DeviceId,
            CommandCatalogueId = job.CommandCatalogueId,
            CommandName = commandName,
            PayloadJson = payloadJson,
            ProviderType = job.ProviderType,
            ExecutionKind = NormalizeExecutionKind(command?.ExecutionKind),
            RollbackKind = NormalizeRollbackKind(command?.RollbackKind),
            Executed = executed,
            Success = success,
            ResultJson = resultJson,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMs = durationMs,
            RollbackOfJobHistoryId = job.RollbackOfJobHistoryId,
            CreatedDate = completedAtUtc
        }, ct);
    }

    private async Task ApplyCompletedJobPoseAsync(Job job, CancellationToken ct)
    {
        var commandName = await _dataAccess.GetCommandCatalogueNameByIdAsync(job.CommandCatalogueId, ct) ?? string.Empty;
        var status = await _deviceStatusDataAccess.GetDeviceStatusByDeviceIdAsync(job.DeviceId, ct);
        if (status == null) return;

        if (commandName.Equals("PLACE", StringComparison.OrdinalIgnoreCase))
        {
            var (placeOk, mapId, x, y, facing) = await TryReadPlacePayloadAsync(job.PayloadJson, job.DeviceId, ct); //async methods cannot use out, so the result comes back as a tuple
            if (!placeOk)
            {
                await InvalidatePoseAsync(status, "PLACE completed but payload could not be parsed.", ct);
                return;
            }

            var map = await _mapDataAccess.GetMapByIdAsync(mapId, ct);
            if (map == null || !map.IsActive || !IsOnMap(map, x, y))
            {
                await InvalidatePoseAsync(status, "PLACE completed but target pose is not inside an active map.", ct);
                return;
            }

            status.PoseMapId = mapId;
            status.GridX = x;
            status.GridY = y;
            status.Facing = facing;
            status.IsGridAligned = true;
            status.IsGridPoseTrusted = true;
            status.PoseConfidence = 1.0;
            status.IsInsideMap = true;
            status.StatusMessage = "Grid pose trusted after PLACE.";
            status.LastSeenAtUtc = DateTime.UtcNow;
            status.ModifiedDate = DateTime.UtcNow;
            await _deviceStatusDataAccess.UpdateDeviceStatusAsync(status.Id, status, ct);
            return;
        }

        if (!DomainConstants.RequiresTrustedGridPose(commandName)) return;

        if (!status.IsGridPoseTrusted || !status.IsGridAligned || !status.PoseMapId.HasValue || !status.GridX.HasValue || !status.GridY.HasValue || string.IsNullOrWhiteSpace(status.Facing))
        {
            await InvalidatePoseAsync(status, "Grid command completed while pose was not trusted. Use PLACE before more grid commands.", ct);
            return;
        }

        var currentMap = await _mapDataAccess.GetMapByIdAsync(status.PoseMapId.Value, ct);
        if (currentMap == null || !currentMap.IsActive)
        {
            await InvalidatePoseAsync(status, "Grid command completed but map is missing or inactive.", ct);
            return;
        }

        if (commandName.Equals("LEFT", StringComparison.OrdinalIgnoreCase))
        {
            status.Facing = TurnLeft(status.Facing);
        }
        else if (commandName.Equals("RIGHT", StringComparison.OrdinalIgnoreCase))
        {
            status.Facing = TurnRight(status.Facing);
        }
        else if (commandName.Equals("MOVE", StringComparison.OrdinalIgnoreCase))
        {
            MoveOneCell(status, +1);
        }
        else if (commandName.Equals("STEP_BACK", StringComparison.OrdinalIgnoreCase))
        {
            MoveOneCell(status, -1);
        }

        if (!status.GridX.HasValue || !status.GridY.HasValue || !IsOnMap(currentMap, status.GridX.Value, status.GridY.Value))
        {
            await InvalidatePoseAsync(status, "Grid command result is outside map bounds.", ct);
            return;
        }

        status.IsGridAligned = true;
        status.IsGridPoseTrusted = true;
        status.PoseConfidence = 1.0;
        status.IsInsideMap = true;
        status.StatusMessage = $"Grid pose updated after {commandName}.";
        status.LastSeenAtUtc = DateTime.UtcNow;
        status.ModifiedDate = DateTime.UtcNow;
        await _deviceStatusDataAccess.UpdateDeviceStatusAsync(status.Id, status, ct);
    }

    private async Task InvalidatePoseAfterFailedMovementAsync(Job job, CancellationToken ct)
    {
        var commandName = await _dataAccess.GetCommandCatalogueNameByIdAsync(job.CommandCatalogueId, ct) ?? string.Empty;
        if (!DomainConstants.RequiresTrustedGridPose(commandName)) return;
        var status = await _deviceStatusDataAccess.GetDeviceStatusByDeviceIdAsync(job.DeviceId, ct);
        if (status == null) return;
        await InvalidatePoseAsync(status, $"Grid pose invalidated because {commandName} failed.", ct);
    }

    private async Task InvalidatePoseAsync(RobotControllerApi.BoundedContexts.DeviceStatuses.Models.DeviceStatus status, string reason, CancellationToken ct)
    {
        status.IsGridAligned = false;
        status.IsGridPoseTrusted = false;
        status.PoseConfidence = 0;
        status.StatusMessage = reason;
        status.ModifiedDate = DateTime.UtcNow;
        await _deviceStatusDataAccess.UpdateDeviceStatusAsync(status.Id, status, ct);
    }

    private async Task<(bool Ok, int MapId, int X, int Y, string Facing)> TryReadPlacePayloadAsync(string payloadJson, int deviceId, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (false, 0, 0, 0, string.Empty);
            var root = document.RootElement;
            var parsedX = TryGetInt(root, "gridX", "GridX", "x", "X");
            var parsedY = TryGetInt(root, "gridY", "GridY", "y", "Y");
            var parsedFacing = TryGetString(root, "facing", "Facing", "direction", "Direction");
            var parsedMapId = TryGetInt(root, "poseMapId", "PoseMapId", "mapId", "MapId") ?? await _dataAccess.GetDeviceMapIdAsync(deviceId, ct);

            var normalizedFacing = string.IsNullOrWhiteSpace(parsedFacing) ? null : NormalizeFacing(parsedFacing!);
            if (!parsedX.HasValue || !parsedY.HasValue || !parsedMapId.HasValue || normalizedFacing == null) return (false, 0, 0, 0, string.Empty);

            return (true, parsedMapId.Value, parsedX.Value, parsedY.Value, normalizedFacing);
        }
        catch (JsonException)
        {
            return (false, 0, 0, 0, string.Empty);
        }
    }

    private static string NormalizeExecutionKind(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        return DomainConstants.IsCommandExecutionKind(candidate) ? candidate : "Mode";
    }

    private static string NormalizeRollbackKind(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        return DomainConstants.IsCommandRollbackKind(candidate) ? candidate : "None";
    }

    private static int? TryGetDurationMs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("durationMs", out var durationElement)) return null;

            return durationElement.ValueKind switch
            {
                JsonValueKind.Number when durationElement.TryGetInt32(out var intValue) && intValue >= 0 => intValue,
                JsonValueKind.String when int.TryParse(durationElement.GetString(), out var stringValue) && stringValue >= 0 => stringValue,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? CalculateDurationMs(DateTime? startedAtUtc, DateTime completedAtUtc)
    {
        if (!startedAtUtc.HasValue) return null;
        return Math.Max(0, (int)Math.Round((completedAtUtc - startedAtUtc.Value).TotalMilliseconds));
    }

    private static readonly string[] CompletableStatuses = { "Executing" };

    // The robot can fail a job it claimed but could not start, so Failed is reachable from Claimed as well.
    private static readonly string[] FailableStatuses = { "Claimed", "Executing" };

    // The authentication handler already proved this credential belongs to deviceId, so no database round trip is needed.
    private static void ValidateAuthenticatedDeviceCanAccessJob(Job existing, int deviceCredentialId, int deviceId)
    {
        if (existing.DeviceId != deviceId) //deviceId comes from the auth claims, the job's device from the database
        {
            throw new UnauthorizedAccessException("This device credential does not own this job's device.");
        }

        ValidateClaimedCredential(existing, deviceCredentialId);
    }

    private static DateTime RequireClaimedAtUtc(DateTime? claimedAtUtc)
    {
        if (!claimedAtUtc.HasValue) throw new ArgumentException("ClaimedAtUtc is required. Send the job.claimedAtUtc value returned by claim-next.");

        // Stored as timestamp without time zone, so compare on the UTC wall-clock value.
        var value = claimedAtUtc.Value.Kind == DateTimeKind.Local ? claimedAtUtc.Value.ToUniversalTime() : claimedAtUtc.Value;
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    private static void ValidateClaimedCredential(Job existing, int? deviceCredentialId)
    {
        if (!deviceCredentialId.HasValue)
        {
            return;
        }

        if (existing.ClaimedByDeviceCredentialId.HasValue && existing.ClaimedByDeviceCredentialId.Value != deviceCredentialId.Value)
        {
            throw new UnauthorizedAccessException("This device credential did not claim this job.");
        }
    }

    private void ValidateJob(int deviceId, int commandCatalogueId, string providerType, string status, string payloadJson, int? workflowId, int? stepNumber, bool isRollback)
    {
        if (deviceId <= 0) throw new ArgumentException("DeviceId is required.");
        if (commandCatalogueId <= 0) throw new ArgumentException("CommandCatalogueId is required.");
        if (!_dataAccess.DeviceExistsAndActive(deviceId)) throw new ArgumentException("Device must exist and be active.");
        if (!_dataAccess.CommandCatalogueExistsAndActive(commandCatalogueId)) throw new ArgumentException("CommandCatalogue must exist and be active.");
        if (!DomainConstants.IsProviderType(providerType)) throw new ArgumentException("ProviderType must be Api, Console, File, Poll, or Rollback.");
        if (!DomainConstants.IsWorkStatus(status)) throw new ArgumentException("Status must be Queued, Claimed, Executing, Completed, Failed, Cancelled, Expired, or RolledBack.");
        if (isRollback && !providerType.Equals("Rollback", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("If IsRollback is true, ProviderType must be Rollback.");
        if (workflowId == null && stepNumber != null) throw new ArgumentException("StepNumber should be null when WorkflowId is null.");
        if (workflowId != null && stepNumber == null) throw new ArgumentException("StepNumber is required when WorkflowId is present.");
        if (stepNumber != null && stepNumber <= 0) throw new ArgumentException("StepNumber must be greater than zero.");
        ValidateJsonObject(payloadJson, "PayloadJson");

        var capability = _dataAccess.GetActiveDeviceCapability(deviceId, commandCatalogueId);
        if (capability == null) throw new ArgumentException("DeviceCapability must exist and be active for this DeviceId and CommandCatalogueId.");
        if (capability.RequiresMap && _dataAccess.GetDeviceMapId(deviceId) == null) throw new ArgumentException("This command requires the device to have an assigned map.");

        var commandName = _dataAccess.GetCommandCatalogueNameById(commandCatalogueId) ?? string.Empty;
        if (workflowId == null && DomainConstants.RequiresTrustedGridPose(commandName) && !_dataAccess.IsDeviceGridPoseTrustedAndAligned(deviceId))
        {
            throw new InvalidOperationException("This grid command requires trusted and aligned device pose.");
        }
    }

    internal static void ValidateJsonObject(string? json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{name} must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{name} must be valid JSON.", ex);
        }
    }

    private static int? TryGetInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
        }

        return null;
    }

    private static string? TryGetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
        }

        return null;
    }

    private static string? NormalizeFacing(string value)
    {
        if (value.Equals("North", StringComparison.OrdinalIgnoreCase)) return "North";
        if (value.Equals("East", StringComparison.OrdinalIgnoreCase)) return "East";
        if (value.Equals("South", StringComparison.OrdinalIgnoreCase)) return "South";
        if (value.Equals("West", StringComparison.OrdinalIgnoreCase)) return "West";
        return null;
    }

    private static string TurnLeft(string facing) => facing switch
    {
        "North" => "West",
        "West" => "South",
        "South" => "East",
        "East" => "North",
        _ => facing
    };

    private static string TurnRight(string facing) => facing switch
    {
        "North" => "East",
        "East" => "South",
        "South" => "West",
        "West" => "North",
        _ => facing
    };

    private static void MoveOneCell(RobotControllerApi.BoundedContexts.DeviceStatuses.Models.DeviceStatus status, int sign)
    {
        if (status.Facing == "North") status.GridY += sign;
        else if (status.Facing == "East") status.GridX += sign;
        else if (status.Facing == "South") status.GridY -= sign;
        else if (status.Facing == "West") status.GridX -= sign;
    }

    private static bool IsOnMap(Map map, int x, int y)
    {
        return x >= 0 && x < map.Columns && y >= 0 && y < map.Rows;
    }

    private static bool IsTerminalStatus(string? status)
    {
        return status != null &&
               (status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Expired", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("RolledBack", StringComparison.OrdinalIgnoreCase));
    }

    public static JobResponse MapToResponse(Job model)
    {
        return new JobResponse
        {
            Id = model.Id,
            DeviceId = model.DeviceId,
            WorkflowId = model.WorkflowId,
            StepNumber = model.StepNumber,
            CommandCatalogueId = model.CommandCatalogueId,
            PayloadJson = model.PayloadJson,
            ProviderType = model.ProviderType,
            Status = model.Status,
            RequestedByAppUserId = model.RequestedByAppUserId,
            ClaimedByDeviceCredentialId = model.ClaimedByDeviceCredentialId,
            ClaimedAtUtc = model.ClaimedAtUtc,
            LeaseExpiresAtUtc = model.LeaseExpiresAtUtc,
            IsRollback = model.IsRollback,
            RollbackOfJobHistoryId = model.RollbackOfJobHistoryId,
            CreatedDate = model.CreatedDate,
            ModifiedDate = model.ModifiedDate
        };
    }
}
