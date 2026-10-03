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

    public bool CancelJob(int id)
    {
        var existing = _dataAccess.GetJobById(id);
        if (existing == null) return false;
        if (existing.Status is "Completed" or "Failed" or "RolledBack") throw new InvalidOperationException("Completed, failed, or rolled-back jobs cannot be cancelled.");
        existing.Status = "Cancelled";
        existing.ModifiedDate = DateTime.UtcNow;
        var updated = _dataAccess.UpdateJob(existing.Id, existing);
        if (updated && existing.WorkflowId.HasValue) FinalizeParentWorkflowIfReady(existing.WorkflowId.Value, DateTime.UtcNow);
        return updated;
    }

    public bool DeactivateJob(int id) => CancelJob(id);

    public bool MarkJobStarted(int id, StartJobRequest request, int deviceCredentialId, int deviceId)
    {
        var claimedAtUtc = RequireClaimedAtUtc(request.ClaimedAtUtc); //the claim id the robot got from claim-next and echoed back
        var existing = _dataAccess.GetJobById(id); //read only to check ownership, never written back
        if (existing == null) return false;
        ValidateAuthenticatedDeviceCanAccessJob(existing, deviceCredentialId, deviceId); //job must belong to the robot's own device

        var now = DateTime.UtcNow;
        var started = _dataAccess.TryMarkClaimedJobExecuting(id, deviceCredentialId, claimedAtUtc, now); //Claimed -> Executing in one guarded UPDATE
        if (started == null) throw new InvalidOperationException("This job cannot be started from its current status."); //row moved on or wrong claim -> 409

        MarkParentWorkflowExecuting(started.WorkflowId, deviceCredentialId, now); //parent workflow follows the job into Executing
        return true;
    }

    public bool MarkJobCompleted(int id, CompleteJobRequest request, int deviceCredentialId, int deviceId)
    {
        var claimedAtUtc = RequireClaimedAtUtc(request.ClaimedAtUtc); //the claim id the robot got from claim-next and echoed back
        var existing = _dataAccess.GetJobById(id); //read only to check ownership, never written back
        if (existing == null) return false;
        ValidateAuthenticatedDeviceCanAccessJob(existing, deviceCredentialId, deviceId); //job must belong to the robot's own device

        var resultJson = string.IsNullOrWhiteSpace(request.ResultJson) ? "{}" : request.ResultJson!;
        ValidateJsonObject(resultJson, "ResultJson");

        var completed = _dataAccess.TryFinishClaimedJob(id, deviceCredentialId, claimedAtUtc, CompletableStatuses, "Completed", DateTime.UtcNow); //Executing -> Completed in one guarded UPDATE
        if (completed == null) throw new InvalidOperationException("This job cannot be completed from its current status."); //row moved on or wrong claim -> 409

        InsertHistory(completed, true, true, resultJson, null, null); //side effects use the row PostgreSQL returned, not the earlier read
        ApplyCompletedJobPose(completed);

        // A standalone rollback job has no workflow to finalize, so this is the only moment
        // its original can be marked reversed.
        if (completed.IsRollback && !completed.WorkflowId.HasValue)
        {
            MarkOriginalsRolledBack(new[] { completed }, completed.ModifiedDate);
        }

        if (completed.WorkflowId.HasValue) FinalizeParentWorkflowIfReady(completed.WorkflowId.Value, DateTime.UtcNow);
        return true;
    }

    public bool MarkJobFailed(int id, FailJobRequest request, int deviceCredentialId, int deviceId)
    {
        var claimedAtUtc = RequireClaimedAtUtc(request.ClaimedAtUtc); //the claim id the robot got from claim-next and echoed back
        var existing = _dataAccess.GetJobById(id); //read only to check ownership, never written back
        if (existing == null) return false;
        ValidateAuthenticatedDeviceCanAccessJob(existing, deviceCredentialId, deviceId); //job must belong to the robot's own device

        existing = _dataAccess.TryFinishClaimedJob(id, deviceCredentialId, claimedAtUtc, FailableStatuses, "Failed", DateTime.UtcNow); //Claimed/Executing -> Failed in one guarded UPDATE, returns the new row
        if (existing == null) throw new InvalidOperationException("This job cannot be failed from its current status."); //row moved on or wrong claim -> 409

        InsertHistory(
            existing,
            false,
            true,
            null,
            string.IsNullOrWhiteSpace(request.FailureCode) ? "JOB_FAILED" : request.FailureCode!.Trim(),
            string.IsNullOrWhiteSpace(request.FailureMessage) ? "Job failed." : request.FailureMessage!.Trim());

        InvalidatePoseAfterFailedMovement(existing);

        if (existing.WorkflowId.HasValue)
        {
            var workflow = _workflowDataAccess.GetWorkflowById(existing.WorkflowId.Value);
            if (workflow != null && workflow.ExecutionMode.Equals("AllOrNothing", StringComparison.OrdinalIgnoreCase))
            {
                CancelQueuedWorkflowJobs(workflow.Id, existing.ModifiedDate);
            }

            FinalizeParentWorkflowIfReady(existing.WorkflowId.Value, DateTime.UtcNow);
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

    private void MarkParentWorkflowExecuting(int? workflowId, int? deviceCredentialId, DateTime now)
    {
        if (!workflowId.HasValue) return;
        var workflow = _workflowDataAccess.GetWorkflowById(workflowId.Value);
        if (workflow == null || IsTerminalStatus(workflow.Status)) return;
        if (deviceCredentialId.HasValue && workflow.ClaimedByDeviceCredentialId == null) workflow.ClaimedByDeviceCredentialId = deviceCredentialId;
        workflow.Status = "Executing";
        workflow.ModifiedDate = now;
        _workflowDataAccess.UpdateWorkflow(workflow.Id, workflow);
    }

    private void FinalizeParentWorkflowIfReady(int workflowId, DateTime now)
    {
        var workflow = _workflowDataAccess.GetWorkflowById(workflowId);
        if (workflow == null || IsTerminalStatus(workflow.Status)) return;

        var jobs = _dataAccess.GetJobsByWorkflowId(workflowId);

        if (workflow.ExecutionMode.Equals("AllOrNothing", StringComparison.OrdinalIgnoreCase) && jobs.Any(x => x.Status is "Failed" or "Expired"))
        {
            CancelQueuedWorkflowJobs(workflowId, now);
            jobs = _dataAccess.GetJobsByWorkflowId(workflowId);
        }

        if (jobs.Any(x => !IsTerminalStatus(x.Status))) return;

        var success = workflow.ExecutionMode.Equals("BestEffort", StringComparison.OrdinalIgnoreCase)
            ? jobs.Any(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            : jobs.All(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase));

        workflow.Status = success ? "Completed" : "Failed";
        workflow.ModifiedDate = now;
        _workflowDataAccess.UpdateWorkflow(workflow.Id, workflow);
        InsertWorkflowHistoryIfMissing(workflow, success, jobs.FirstOrDefault(x => !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))?.StepNumber, success ? null : "Workflow finished with failed steps.", now);

        // Only a rollback that actually finished reverses anything. A failed one leaves the
        // original standing, which is the honest record.
        if (workflow.IsRollback && success)
        {
            MarkOriginalsRolledBack(jobs, now);
        }
    }

    // Marks the work a completed rollback has undone, so the original stops reading as though
    // it still stands. Without this the RolledBack status was never written by anything: the
    // original workflow sat at Completed forever, and nothing recorded that it had been reversed.
    //
    // The link is each rollback job's RollbackOfJobHistoryId, which points at the history row of
    // the step it reverses. That row carries both the original job and its workflow, so one
    // lookup covers jobs and workflows on both rollback paths.
    private void MarkOriginalsRolledBack(IEnumerable<Job> rollbackJobs, DateTime now)
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
            var history = _jobHistoryDataAccess.GetJobHistoryById(historyId);
            if (history == null) continue;
            if (history.JobId.HasValue) originalJobIds.Add(history.JobId.Value);
            if (history.WorkflowId.HasValue) originalWorkflowIds.Add(history.WorkflowId.Value);
        }

        foreach (var jobId in originalJobIds)
        {
            var job = _dataAccess.GetJobById(jobId);

            // Only completed work can have been undone. A step that failed or was cancelled
            // never happened, so there is nothing to reverse and its status stays as it is.
            if (job == null || job.IsRollback) continue;
            if (!job.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) continue;

            job.Status = "RolledBack";
            job.ModifiedDate = now;
            _dataAccess.UpdateJob(job.Id, job);
        }

        foreach (var workflowId in originalWorkflowIds)
        {
            var workflow = _workflowDataAccess.GetWorkflowById(workflowId);
            if (workflow == null || workflow.IsRollback) continue;
            if (!workflow.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) continue;

            workflow.Status = "RolledBack";
            workflow.ModifiedDate = now;
            _workflowDataAccess.UpdateWorkflow(workflow.Id, workflow);
        }
    }

    private void CancelQueuedWorkflowJobs(int workflowId, DateTime now)
    {
        foreach (var queuedJob in _dataAccess.GetJobsByWorkflowId(workflowId).Where(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)))
        {
            _dataAccess.TryUpdateQueuedJobStatus(queuedJob.Id, "Cancelled", now); //Queued -> Cancelled, no-op if something claimed it meanwhile
        }
    }

    private void InsertWorkflowHistoryIfMissing(Workflow workflow, bool success, int? failedStepNumber, string? failureMessage, DateTime now)
    {
        if (_workflowHistoryDataAccess.GetWorkflowHistoriesByWorkflowId(workflow.Id).Any()) return;

        _workflowHistoryDataAccess.InsertWorkflowHistory(new WorkflowHistory
        {
            WorkflowId = workflow.Id,
            DeviceId = workflow.DeviceId,
            ProviderType = workflow.ProviderType,
            Status = success ? "Completed" : "Failed",
            Executed = _dataAccess.GetJobsByWorkflowId(workflow.Id).Any(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            Success = success,
            StartedAtUtc = workflow.ClaimedAtUtc,
            CompletedAtUtc = now,
            FailedStepNumber = failedStepNumber,
            FailureMessage = failureMessage,
            RollbackOfWorkflowHistoryId = workflow.RollbackOfWorkflowHistoryId,
            CreatedDate = now
        });
    }

    private void InsertHistory(Job job, bool success, bool executed, string? resultJson, string? failureCode, string? failureMessage)
    {
        var command = _dataAccess.GetCommandCatalogueById(job.CommandCatalogueId);
        var commandName = command?.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(commandName)) commandName = $"CommandCatalogue:{job.CommandCatalogueId}";

        var completedAtUtc = DateTime.UtcNow;
        var startedAtUtc = executed ? job.ClaimedAtUtc : null;
        var payloadJson = string.IsNullOrWhiteSpace(job.PayloadJson) ? "{}" : job.PayloadJson;
        var durationMs = executed
            ? TryGetDurationMs(payloadJson) ?? TryGetDurationMs(resultJson) ?? CalculateDurationMs(startedAtUtc, completedAtUtc)
            : null;

        _jobHistoryDataAccess.InsertJobHistory(new JobHistory
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
        });
    }

    private void ApplyCompletedJobPose(Job job)
    {
        var commandName = _dataAccess.GetCommandCatalogueNameById(job.CommandCatalogueId) ?? string.Empty;
        var status = _deviceStatusDataAccess.GetDeviceStatusByDeviceId(job.DeviceId);
        if (status == null) return;

        if (commandName.Equals("PLACE", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadPlacePayload(job.PayloadJson, job.DeviceId, out var mapId, out var x, out var y, out var facing))
            {
                InvalidatePose(status, "PLACE completed but payload could not be parsed.");
                return;
            }

            var map = _mapDataAccess.GetMapById(mapId);
            if (map == null || !map.IsActive || !IsOnMap(map, x, y))
            {
                InvalidatePose(status, "PLACE completed but target pose is not inside an active map.");
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
            _deviceStatusDataAccess.UpdateDeviceStatus(status.Id, status);
            return;
        }

        if (!DomainConstants.RequiresTrustedGridPose(commandName)) return;

        if (!status.IsGridPoseTrusted || !status.IsGridAligned || !status.PoseMapId.HasValue || !status.GridX.HasValue || !status.GridY.HasValue || string.IsNullOrWhiteSpace(status.Facing))
        {
            InvalidatePose(status, "Grid command completed while pose was not trusted. Use PLACE before more grid commands.");
            return;
        }

        var currentMap = _mapDataAccess.GetMapById(status.PoseMapId.Value);
        if (currentMap == null || !currentMap.IsActive)
        {
            InvalidatePose(status, "Grid command completed but map is missing or inactive.");
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
            InvalidatePose(status, "Grid command result is outside map bounds.");
            return;
        }

        status.IsGridAligned = true;
        status.IsGridPoseTrusted = true;
        status.PoseConfidence = 1.0;
        status.IsInsideMap = true;
        status.StatusMessage = $"Grid pose updated after {commandName}.";
        status.LastSeenAtUtc = DateTime.UtcNow;
        status.ModifiedDate = DateTime.UtcNow;
        _deviceStatusDataAccess.UpdateDeviceStatus(status.Id, status);
    }

    private void InvalidatePoseAfterFailedMovement(Job job)
    {
        var commandName = _dataAccess.GetCommandCatalogueNameById(job.CommandCatalogueId) ?? string.Empty;
        if (!DomainConstants.RequiresTrustedGridPose(commandName)) return;
        var status = _deviceStatusDataAccess.GetDeviceStatusByDeviceId(job.DeviceId);
        if (status == null) return;
        InvalidatePose(status, $"Grid pose invalidated because {commandName} failed.");
    }

    private void InvalidatePose(RobotControllerApi.BoundedContexts.DeviceStatuses.Models.DeviceStatus status, string reason)
    {
        status.IsGridAligned = false;
        status.IsGridPoseTrusted = false;
        status.PoseConfidence = 0;
        status.StatusMessage = reason;
        status.ModifiedDate = DateTime.UtcNow;
        _deviceStatusDataAccess.UpdateDeviceStatus(status.Id, status);
    }

    private bool TryReadPlacePayload(string payloadJson, int deviceId, out int mapId, out int x, out int y, out string facing)
    {
        mapId = 0;
        x = 0;
        y = 0;
        facing = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = document.RootElement;
            var parsedX = TryGetInt(root, "gridX", "GridX", "x", "X");
            var parsedY = TryGetInt(root, "gridY", "GridY", "y", "Y");
            var parsedFacing = TryGetString(root, "facing", "Facing", "direction", "Direction");
            var parsedMapId = TryGetInt(root, "poseMapId", "PoseMapId", "mapId", "MapId") ?? _dataAccess.GetDeviceMapId(deviceId);

            var normalizedFacing = string.IsNullOrWhiteSpace(parsedFacing) ? null : NormalizeFacing(parsedFacing!);
            if (!parsedX.HasValue || !parsedY.HasValue || !parsedMapId.HasValue || normalizedFacing == null) return false;

            mapId = parsedMapId.Value;
            x = parsedX.Value;
            y = parsedY.Value;
            facing = normalizedFacing;
            return true;
        }
        catch (JsonException)
        {
            return false;
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
