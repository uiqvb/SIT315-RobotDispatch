using System.Text.Json;
using RobotControllerApi.BoundedContexts.DeviceStatuses.Models;
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

public class WorkDispatchService : IWorkDispatchService
{
    private readonly IJobDataAccess _jobDataAccess;
    private readonly IWorkflowDataAccess _workflowDataAccess;
    private readonly IJobHistoryDataAccess _jobHistoryDataAccess;
    private readonly IWorkflowHistoryDataAccess _workflowHistoryDataAccess;
    private readonly IDeviceStatusDataAccess _deviceStatusDataAccess;
    private readonly IMapDataAccess _mapDataAccess;
    private readonly IConfiguration _configuration;

    public WorkDispatchService(
        IJobDataAccess jobDataAccess,
        IWorkflowDataAccess workflowDataAccess,
        IJobHistoryDataAccess jobHistoryDataAccess,
        IWorkflowHistoryDataAccess workflowHistoryDataAccess,
        IDeviceStatusDataAccess deviceStatusDataAccess,
        IMapDataAccess mapDataAccess,
        IConfiguration configuration)
    {
        _jobDataAccess = jobDataAccess;
        _workflowDataAccess = workflowDataAccess;
        _jobHistoryDataAccess = jobHistoryDataAccess;
        _workflowHistoryDataAccess = workflowHistoryDataAccess;
        _deviceStatusDataAccess = deviceStatusDataAccess;
        _mapDataAccess = mapDataAccess;
        _configuration = configuration;
    }

    public async Task<WorkItemClaimResponse?> ClaimNextWorkItemAsync(int deviceId, ClaimJobRequest request, int deviceCredentialId, int authenticatedDeviceId, CancellationToken ct = default)
    {
        if (deviceId <= 0) throw new ArgumentException("DeviceId is required.");
        if (deviceCredentialId <= 0) throw new ArgumentException("DeviceCredentialId is required.");
        if (authenticatedDeviceId != deviceId) throw new InvalidOperationException("DeviceCredential does not own the route device."); //route device vs device from auth claims, no DB query

        var now = DateTime.UtcNow;
        var configuredLeaseMinutes = int.TryParse(_configuration["WorkDispatch:LeaseMinutes"], out var parsedLeaseMinutes) ? parsedLeaseMinutes : 5;
        var leaseMinutes = request.LeaseMinutes > 0 ? request.LeaseMinutes : configuredLeaseMinutes;

        // Stale cleanup no longer runs here; StaleWorkExpiryService owns it on a 15 s timer.

        // a step that can be physically dispatched. The limit prevents an accidental
        // infinite loop if data is corrupt.
        for (var i = 0; i < 50; i++) //search for oldest job, if corrupt then search for next, if corrupt next... do this at most 50 times. 
        {
            var job = await _jobDataAccess.GetOldestQueuedJobByDeviceIdAsync(deviceId, ct); //F1: fetches the job. 
            if (job == null) return null;

            var decision = await PrepareQueuedJobForDispatchAsync(job, now, ct);
            if (decision == DispatchDecision.SkipAndContinue)
            {
                continue; //break iteration if the current job is not ready to go(aka skipandcontinue decision), move to next iteration
            }

            //the job which was status=queud becomes stauts=claimed, but only if no other request got there first.
            var claimed = await _jobDataAccess.TryClaimJobAsync(job.Id, deviceCredentialId, now, now.AddMinutes(leaseMinutes), ct); //PostgreSQL picks one winner, returns the stored row
            if (claimed == null) continue; //another request won this job, go look for the next one

            await MarkParentWorkflowClaimedAsync(claimed.WorkflowId, deviceCredentialId, now, leaseMinutes, ct); //parent workflow is also marked then

            var (inverseCommandName, inversePayloadJson) = await BuildCachedInverseAsync(claimed, ct); //inverse is built

            return new WorkItemClaimResponse
            {
                WorkItemType = "Job",
                Job = JobService.MapToResponse(claimed), //job and inverse are then sent back to the robot as response.
                InverseCommandName = inverseCommandName,
                InversePayloadJson = inversePayloadJson
            };
        }

        return null;
    }

    // Stale-expiry used to run only inside claim-next, so a robot that stopped polling never
    // drained its own queue: its lease never expired, and the per-device cap then rejected
    // every new job. A robot that dies mid-demo would brick the queue until restarted by hand.
    // Running the same sweep on a timer removes the dependency on the dead robot polling.
    public async Task<int> ExpireStaleWorkForAllDevicesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var staleJobs = await _jobDataAccess.GetStaleJobsAsync(now, ct); //PostgreSQL returns only expired rows across every device

        var settled = 0;
        foreach (var staleJob in staleJobs)
        {
            if (await SettleStaleJobAsync(staleJob, now, ct)) settled++; //each job settled straight from this list, no second query per device
        }

        return settled;
    }

    public async Task<OfflineRollbackReconcileResponse> ReportOfflineRollbackAsync(int deviceId, ReportOfflineRollbackRequest request, int deviceCredentialId, int authenticatedDeviceId, CancellationToken ct = default)
    {
        if (deviceId <= 0) throw new ArgumentException("DeviceId is required.");
        if (deviceCredentialId <= 0) throw new ArgumentException("DeviceCredentialId is required.");
        if (authenticatedDeviceId != deviceId) throw new InvalidOperationException("DeviceCredential does not own the route device."); //route device vs device from auth claims, no DB query
        if (request.Steps.Count == 0) throw new ArgumentException("At least one executed rollback step is required.");

        var now = DateTime.UtcNow;

        // Settle whatever the disconnect stranded first, so work the robot abandoned when it
        // went dark does not keep blocking the queue behind this reconciliation.
        await ExpireStaleWorkAsync(deviceId, now, ct);

        var status = await _deviceStatusDataAccess.GetDeviceStatusByDeviceIdAsync(deviceId, ct);
        if (status == null) throw new InvalidOperationException("Device has no status row to reconcile against.");

        // Deliberately NOT requiring trusted pose. Losing the connection is precisely what
        // dropped trust (ExpireStaleWork -> InvalidatePose), so demanding it here would make
        // reconciliation impossible in the exact case it exists for. Invalidation clears the
        // flags but leaves the coordinates, and those coordinates are the anchor we replay from.
        var mapId = status.PoseMapId ?? await _jobDataAccess.GetDeviceMapIdAsync(deviceId, ct);
        if (!mapId.HasValue || !status.GridX.HasValue || !status.GridY.HasValue || string.IsNullOrWhiteSpace(status.Facing))
        {
            return await RejectReconciliationAsync(status, request.Steps.Count, "No last known grid pose to reconcile from. PLACE the robot before reporting an offline rollback.", now, ct);
        }

        var map = await _mapDataAccess.GetMapByIdAsync(mapId.Value, ct);
        if (map == null || !map.IsActive)
        {
            return await RejectReconciliationAsync(status, request.Steps.Count, "Reconciliation requires an active map.", now, ct);
        }

        // Simulate the whole report before committing any of it. A half-applied rollback would
        // leave the backend recording a position the robot was never at, which is worse than
        // admitting we are lost.
        var pose = new GridPose { MapId = mapId, X = status.GridX, Y = status.GridY, Facing = status.Facing, IsAligned = true, IsTrusted = true };

        foreach (var step in request.Steps)
        {
            var commandName = step.CommandName?.Trim() ?? string.Empty;
            if (commandName.Length == 0)
            {
                return await RejectReconciliationAsync(status, request.Steps.Count, "A reported rollback step had no command name.", now, ct);
            }

            if (!TryApplyReportedStep(commandName, pose, map, out var failure))
            {
                return await RejectReconciliationAsync(status, request.Steps.Count, $"Reported rollback step {commandName} could not be reconciled: {failure}", now, ct);
            }
        }

        // The whole sequence holds together, so adopt it and hand grid work back to the robot.
        status.PoseMapId = pose.MapId;
        status.GridX = pose.X;
        status.GridY = pose.Y;
        status.Facing = pose.Facing;
        status.IsGridAligned = true;
        status.IsGridPoseTrusted = true;
        status.PoseConfidence = 1.0;
        status.IsInsideMap = true;
        status.ConnectionState = "Online";
        status.StatusMessage = string.IsNullOrWhiteSpace(request.Reason)
            ? $"Grid pose resynced after the robot reported {request.Steps.Count} offline rollback step(s)."
            : $"Grid pose resynced after offline rollback: {request.Reason.Trim()}";
        status.LastSeenAtUtc = now;
        status.ModifiedDate = now;
        await _deviceStatusDataAccess.UpdateDeviceStatusAsync(status.Id, status, ct);

        var touchedWorkflowIds = new HashSet<int>();

        foreach (var step in request.Steps)
        {
            await RecordOfflineRollbackStepAsync(deviceId, step, now, ct);
            await MarkOriginalJobRolledBackAsync(step.RollbackOfJobId, deviceId, touchedWorkflowIds, now, ct);
        }

        foreach (var workflowId in touchedWorkflowIds)
        {
            await MarkWorkflowRolledBackIfAnyStepReversedAsync(workflowId, now, ct);
        }

        return new OfflineRollbackReconcileResponse
        {
            StepsAccepted = request.Steps.Count,
            StepsRejected = 0,
            GridX = status.GridX,
            GridY = status.GridY,
            Facing = status.Facing,
            IsGridPoseTrusted = true,
            Message = status.StatusMessage ?? "Grid pose resynced."
        };
    }

    // Applies one reported inverse to the simulated pose. Mirrors ApplyCompletedJobPose so a
    // step replayed on reconnect lands the robot exactly where reporting it online would have.
    private static bool TryApplyReportedStep(string commandName, GridPose pose, Map map, out string failure)
    {
        failure = string.Empty;

        if (commandName.Equals("LEFT", StringComparison.OrdinalIgnoreCase))
        {
            pose.Facing = TurnLeft(pose.Facing!);
            return true;
        }

        if (commandName.Equals("RIGHT", StringComparison.OrdinalIgnoreCase))
        {
            pose.Facing = TurnRight(pose.Facing!);
            return true;
        }

        if (commandName.Equals("MOVE", StringComparison.OrdinalIgnoreCase))
        {
            MoveOneCell(pose, +1);
        }
        else if (commandName.Equals("STEP_BACK", StringComparison.OrdinalIgnoreCase))
        {
            MoveOneCell(pose, -1);
        }
        else
        {
            // Non-grid commands cannot move the robot, so they reconcile trivially.
            if (!DomainConstants.RequiresTrustedGridPose(commandName)) return true;

            failure = "command is not a recognised grid movement";
            return false;
        }

        if (!IsOnMap(map, pose.X!.Value, pose.Y!.Value))
        {
            failure = "it lands outside the map";
            return false;
        }

        return true;
    }

    private async Task<OfflineRollbackReconcileResponse> RejectReconciliationAsync(DeviceStatus status, int stepCount, string message, DateTime now, CancellationToken ct)
    {
        await InvalidatePoseAsync(status.DeviceId, message, now, ct);

        return new OfflineRollbackReconcileResponse
        {
            StepsAccepted = 0,
            StepsRejected = stepCount,
            GridX = status.GridX,
            GridY = status.GridY,
            Facing = status.Facing,
            IsGridPoseTrusted = false,
            Message = message
        };
    }

    // Offline steps have no Job row of their own — they were never dispatched — so the history
    // row is written directly. Without it the robot's offline movements leave no audit trail.
    private async Task RecordOfflineRollbackStepAsync(int deviceId, OfflineRollbackStepReport step, DateTime now, CancellationToken ct)
    {
        var commandName = step.CommandName.Trim();
        var commandCatalogueId = await _jobDataAccess.GetCommandCatalogueIdByNameAsync(commandName, ct);
        var command = commandCatalogueId.HasValue ? await _jobDataAccess.GetCommandCatalogueByIdAsync(commandCatalogueId.Value, ct) : null;

        await _jobHistoryDataAccess.InsertJobHistoryAsync(new JobHistory
        {
            JobId = null,
            DeviceId = deviceId,
            CommandCatalogueId = commandCatalogueId,
            CommandName = commandName,
            PayloadJson = string.IsNullOrWhiteSpace(step.PayloadJson) ? "{}" : step.PayloadJson,
            ProviderType = "Rollback",
            ExecutionKind = NormalizeExecutionKind(command?.ExecutionKind),
            RollbackKind = NormalizeRollbackKind(command?.RollbackKind),
            Executed = true,
            Success = true,
            FailureMessage = null,
            StartedAtUtc = step.ExecutedAtUtc,
            CompletedAtUtc = step.ExecutedAtUtc ?? now,
            CreatedDate = now
        }, ct);
    }

    // The work the robot undid should not stay Completed, or the dashboard shows the robot
    // having done something it has since reversed.
    private async Task MarkOriginalJobRolledBackAsync(int? jobId, int deviceId, HashSet<int> touchedWorkflowIds, DateTime now, CancellationToken ct)
    {
        if (!jobId.HasValue) return;

        var job = await _jobDataAccess.GetJobByIdAsync(jobId.Value, ct);
        if (job == null || job.DeviceId != deviceId) return;
        if (job.Status.Equals("RolledBack", StringComparison.OrdinalIgnoreCase)) return;

        job.Status = "RolledBack";
        job.ModifiedDate = now;
        await _jobDataAccess.UpdateJobAsync(job.Id, job, ct);

        if (job.WorkflowId.HasValue) touchedWorkflowIds.Add(job.WorkflowId.Value);
    }

    // A workflow whose steps have been reversed should not still read Completed.
    //
    // Deliberately "any step", not "all steps": an offline burst reverses a fixed chunk, so it
    // routinely undoes part of a workflow — five of six in the first hardware test. Requiring
    // all steps would leave the common case reading Completed over a pile of RolledBack rows,
    // which is the confusing display this fixes. The schema has no PartiallyRolledBack status,
    // and adding one would be a migration.
    private async Task MarkWorkflowRolledBackIfAnyStepReversedAsync(int workflowId, DateTime now, CancellationToken ct)
    {
        var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(workflowId, ct);
        if (workflow == null || workflow.IsRollback) return;
        if (workflow.Status.Equals("RolledBack", StringComparison.OrdinalIgnoreCase)) return;

        var jobs = await _jobDataAccess.GetJobsByWorkflowIdAsync(workflowId, ct);
        if (!jobs.Any(x => x.Status.Equals("RolledBack", StringComparison.OrdinalIgnoreCase))) return;

        workflow.Status = "RolledBack";
        workflow.ModifiedDate = now;
        await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);

        // Cancel the steps that never ran. A rolled-back workflow is not going to be resumed —
        // the robot has physically walked back out of the position those steps were planned
        // from, so dispatching them later would drive it somewhere nobody asked for.
        //
        // This has to happen here rather than at dispatch time. GetOldestQueuedJobByDeviceId
        // only returns jobs whose parent workflow is Queued/Claimed/Executing, so the moment
        // the workflow becomes RolledBack its leftover steps are invisible to the dispatcher —
        // which means PrepareQueuedJobForDispatch's terminal-status check is never reached for
        // them. They would otherwise sit at "Queued" forever, cluttering the console and making
        // it look as though cancelled work is still pending.
        foreach (var job in jobs)
        {
            if (job.Status is "Completed" or "Failed" or "Cancelled" or "RolledBack") continue;

            job.Status = "Cancelled";
            job.ModifiedDate = now;
            await _jobDataAccess.UpdateJobAsync(job.Id, job, ct);
        }
    }

    // The inverse handed to the robot alongside the command, for its local undo stack.
    //
    // Only Exact-kind commands qualify. BestEffort inverses are timed reversals that
    // accumulate dead-reckoning error, and an offline robot replaying its stack gets none
    // of the server-side pre-flight validation that normally catches a bad step — so the
    // one situation where the inverse is used is the one where it can least be checked.
    // A missing inverse is a normal answer here, never an error: dispatch must not fail
    // because a command happens to be irreversible.
    private async Task<(string? CommandName, string? PayloadJson)> BuildCachedInverseAsync(Job job, CancellationToken ct)
    {
        try
        {
            var original = await _jobDataAccess.GetCommandCatalogueByIdAsync(job.CommandCatalogueId, ct);
            if (original == null) return (null, null);

            var originalCommand = ToCompensationCommand(original);
            var rollbackKind = CompensationBuilder.ResolveRollbackKind(null, originalCommand);
            if (!CompensationBuilder.IsExact(rollbackKind)) return (null, null);
            if (string.IsNullOrWhiteSpace(originalCommand.InverseCommandName)) return (null, null);

            var inverseName = originalCommand.InverseCommandName.Trim();
            var inverseId = await _jobDataAccess.GetCommandCatalogueIdByNameAsync(inverseName, ct);
            if (!inverseId.HasValue) return (null, null);

            var inverse = await _jobDataAccess.GetCommandCatalogueByIdAsync(inverseId.Value, ct);
            if (inverse == null || !inverse.IsActive) return (null, null);

            // The robot can only replay what it is physically capable of executing.
            if (await _jobDataAccess.GetActiveDeviceCapabilityAsync(job.DeviceId, inverse.Id, ct) == null) return (null, null);

            var inverseCommand = ToCompensationCommand(inverse);
            var payload = CompensationBuilder.TransformPayload(originalCommand, inverseCommand, job.PayloadJson, null, rollbackKind);
            return (inverse.Name, payload);
        }
        catch (InvalidOperationException)
        {
            // No transformable payload (e.g. a required duration the job never carried).
            return (null, null);
        }
    }

    private static CompensationCommand ToCompensationCommand(CommandCatalogueSnapshot snapshot) => new(
        snapshot.Name,
        snapshot.ExecutionKind,
        snapshot.RollbackKind,
        snapshot.InverseCommandName,
        snapshot.RequiresDuration);

    private async Task<DispatchDecision> PrepareQueuedJobForDispatchAsync(Job job, DateTime now, CancellationToken ct)
    {
        if (!job.WorkflowId.HasValue)
        {
            var standaloneValidation = await ValidateJobAgainstCurrentPoseAsync(job, ct);
            if (standaloneValidation.IsValid) return DispatchDecision.Claim;

            await MarkJobValidationFailedAsync(job, standaloneValidation.FailureCode, standaloneValidation.FailureMessage, false, now, ct);
            return DispatchDecision.SkipAndContinue;
        }

        var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(job.WorkflowId.Value, ct);
        if (workflow == null)
        {
            await MarkJobValidationFailedAsync(job, "WORKFLOW_MISSING", "Parent workflow could not be found.", false, now, ct);
            return DispatchDecision.SkipAndContinue;
        }

        if (IsTerminalStatus(workflow.Status))
        {
            await _jobDataAccess.TryUpdateQueuedJobStatusAsync(job.Id, "Cancelled", now, ct); //Queued -> Cancelled, no-op if something claimed it meanwhile
            return DispatchDecision.SkipAndContinue;
        }

        if (workflow.ExecutionMode.Equals("AllOrNothing", StringComparison.OrdinalIgnoreCase))
        {
            var allOrNothingValidation = await ValidateRemainingWorkflowStepsAsync(workflow, ct);
            if (!allOrNothingValidation.IsValid)
            {
                await FailWorkflowBeforeDispatchAsync(workflow, allOrNothingValidation.Job, allOrNothingValidation.FailureCode, allOrNothingValidation.FailureMessage, now, ct);
                return DispatchDecision.SkipAndContinue;
            }

            return DispatchDecision.Claim;
        }

        var stepValidation = await ValidateJobAgainstCurrentPoseAsync(job, ct);
        if (stepValidation.IsValid) return DispatchDecision.Claim;

        await MarkJobValidationFailedAsync(job, stepValidation.FailureCode, stepValidation.FailureMessage, false, now, ct);
        await FinalizeParentWorkflowIfReadyAsync(workflow.Id, now, ct);
        return DispatchDecision.SkipAndContinue;
    }

    private async Task<ValidationResult> ValidateRemainingWorkflowStepsAsync(Workflow workflow, CancellationToken ct)
    {
        var queuedJobs = (await _jobDataAccess.GetJobsByWorkflowIdAsync(workflow.Id, ct))
            .Where(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.StepNumber)
            .ThenBy(x => x.Id)
            .ToList();

        var pose = await BuildCurrentPoseAsync(workflow.DeviceId, ct);

        foreach (var queuedJob in queuedJobs)
        {
            var commandName = await _jobDataAccess.GetCommandCatalogueNameByIdAsync(queuedJob.CommandCatalogueId, ct) ?? string.Empty;
            var validation = await ValidateAndSimulateAsync(commandName, queuedJob.PayloadJson, workflow.DeviceId, pose, ct);
            if (!validation.IsValid)
            {
                validation.Job = queuedJob;
                return validation;
            }

            pose = validation.NextPose ?? pose;
        }

        return ValidationResult.Valid();
    }

    private async Task<ValidationResult> ValidateJobAgainstCurrentPoseAsync(Job job, CancellationToken ct)
    {
        var commandName = await _jobDataAccess.GetCommandCatalogueNameByIdAsync(job.CommandCatalogueId, ct) ?? string.Empty;
        var pose = await BuildCurrentPoseAsync(job.DeviceId, ct);
        return await ValidateAndSimulateAsync(commandName, job.PayloadJson, job.DeviceId, pose, ct);
    }

    private async Task<ValidationResult> ValidateAndSimulateAsync(string commandName, string payloadJson, int deviceId, GridPose pose, CancellationToken ct)
    {
        if (commandName.Equals("PLACE", StringComparison.OrdinalIgnoreCase))
        {
            var (placed, placedPose, failure) = await TryReadPlacePoseAsync(payloadJson, deviceId, ct); //async methods cannot use out, so the result comes back as a tuple
            return placed
                ? ValidationResult.Valid(placedPose)
                : ValidationResult.Invalid("PLACE_PAYLOAD_INVALID", failure ?? "PLACE requires gridX/gridY/facing payload.");
        }

        if (!DomainConstants.RequiresTrustedGridPose(commandName))
        {
            return ValidationResult.Valid(pose);
        }

        if (!pose.IsTrusted || !pose.IsAligned || !pose.MapId.HasValue || !pose.X.HasValue || !pose.Y.HasValue || string.IsNullOrWhiteSpace(pose.Facing))
        {
            return ValidationResult.Invalid("GRID_POSE_UNTRUSTED", "Grid movement requires trusted and aligned grid pose. Use PLACE first.");
        }

        var map = await _mapDataAccess.GetMapByIdAsync(pose.MapId.Value, ct);
        if (map == null || !map.IsActive)
        {
            return ValidationResult.Invalid("MAP_NOT_AVAILABLE", "Grid movement requires an active map.");
        }

        var next = pose.Clone();

        if (commandName.Equals("LEFT", StringComparison.OrdinalIgnoreCase))
        {
            next.Facing = TurnLeft(pose.Facing!);
            return ValidationResult.Valid(next);
        }

        if (commandName.Equals("RIGHT", StringComparison.OrdinalIgnoreCase))
        {
            next.Facing = TurnRight(pose.Facing!);
            return ValidationResult.Valid(next);
        }

        if (commandName.Equals("MOVE", StringComparison.OrdinalIgnoreCase))
        {
            MoveOneCell(next, +1);
        }
        else if (commandName.Equals("STEP_BACK", StringComparison.OrdinalIgnoreCase))
        {
            MoveOneCell(next, -1);
        }
        else
        {
            return ValidationResult.Valid(pose);
        }

        if (!IsOnMap(map, next.X!.Value, next.Y!.Value))
        {
            return ValidationResult.Invalid("GRID_OUT_OF_BOUNDS", $"{commandName} would move the robot outside the map.");
        }

        return ValidationResult.Valid(next);
    }

    private async Task ExpireStaleWorkAsync(int deviceId, DateTime now, CancellationToken ct)
    {
        var staleJobs = await _jobDataAccess.GetStaleJobsByDeviceIdAsync(deviceId, now, ct); //PostgreSQL returns only expired Claimed/Executing rows

        foreach (var staleJob in staleJobs)
        {
            await SettleStaleJobAsync(staleJob, now, ct);
        }
    }

    // Returns true only if this call actually changed the row.
    private async Task<bool> SettleStaleJobAsync(Job staleJob, DateTime now, CancellationToken ct)
    {
        // Each transition re-checks the row, so a robot that started or finished the job since the SELECT wins.
        if (staleJob.Status.Equals("Claimed", StringComparison.OrdinalIgnoreCase))
        {
            return await _jobDataAccess.TryRequeueStaleClaimedJobAsync(staleJob.Id, staleJob.ClaimedAtUtc, now, ct); //Claimed -> Queued, only if it is still this stale claim
        }

        if (!await _jobDataAccess.TryExpireStaleExecutingJobAsync(staleJob.Id, staleJob.ClaimedAtUtc, now, ct)) return false; //Executing -> Expired; robot finished first, so skip side effects

        staleJob.Status = "Expired";
        staleJob.ModifiedDate = now;
        await InsertJobHistoryAsync(staleJob, false, true, null, "LEASE_EXPIRED", "Job lease expired while executing. It was not automatically retried.", now, ct);
        await InvalidatePoseAsync(staleJob.DeviceId, "Grid pose invalidated because an executing job expired.", now, ct);

        if (staleJob.WorkflowId.HasValue)
        {
            var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(staleJob.WorkflowId.Value, ct);
            if (workflow != null)
            {
                await CancelQueuedWorkflowJobsAsync(workflow.Id, now, ct);
                workflow.Status = "Failed";
                workflow.ModifiedDate = now;
                await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
                await InsertWorkflowHistoryIfMissingAsync(workflow, false, staleJob.StepNumber, "A workflow step expired while executing.", now, ct);
            }
        }

        return true;
    }

    private async Task MarkParentWorkflowClaimedAsync(int? workflowId, int deviceCredentialId, DateTime now, int leaseMinutes, CancellationToken ct)
    {
        if (!workflowId.HasValue)
        {
            return;
        }

        var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(workflowId.Value, ct);
        if (workflow == null)
        {
            return;
        }

        if (IsTerminalStatus(workflow.Status))
        {
            return;
        }

        workflow.Status = workflow.Status == "Executing" ? "Executing" : "Claimed";
        workflow.ClaimedByDeviceCredentialId ??= deviceCredentialId;
        workflow.ClaimedAtUtc ??= now;
        workflow.LeaseExpiresAtUtc = now.AddMinutes(leaseMinutes);
        workflow.ModifiedDate = now;
        await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
    }

    private async Task MarkJobValidationFailedAsync(Job job, string failureCode, string failureMessage, bool executed, DateTime now, CancellationToken ct)
    {
        // Only a still-Queued job is failed, so a request that lost the claim race cannot fail the winner's job.
        if (!await _jobDataAccess.TryUpdateQueuedJobStatusAsync(job.Id, "Failed", now, ct)) return;

        job.Status = "Failed";
        job.ModifiedDate = now;
        await InsertJobHistoryAsync(job, false, executed, null, failureCode, failureMessage, now, ct);
    }

    private async Task FailWorkflowBeforeDispatchAsync(Workflow workflow, Job? failedJob, string failureCode, string failureMessage, DateTime now, CancellationToken ct)
    {
        if (failedJob != null)
        {
            await MarkJobValidationFailedAsync(failedJob, failureCode, failureMessage, false, now, ct);
        }

        await CancelQueuedWorkflowJobsAsync(workflow.Id, now, ct);
        workflow.Status = "Failed";
        workflow.ModifiedDate = now;
        await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
        await InsertWorkflowHistoryIfMissingAsync(workflow, false, failedJob?.StepNumber, failureMessage, now, ct);
    }

    private async Task CancelQueuedWorkflowJobsAsync(int workflowId, DateTime now, CancellationToken ct)
    {
        foreach (var job in (await _jobDataAccess.GetJobsByWorkflowIdAsync(workflowId, ct)).Where(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)))
        {
            await _jobDataAccess.TryUpdateQueuedJobStatusAsync(job.Id, "Cancelled", now, ct); //Queued -> Cancelled, no-op if something claimed it meanwhile
        }
    }

    private async Task FinalizeParentWorkflowIfReadyAsync(int workflowId, DateTime now, CancellationToken ct)
    {
        var workflow = await _workflowDataAccess.GetWorkflowByIdAsync(workflowId, ct);
        if (workflow == null || IsTerminalStatus(workflow.Status)) return;

        var jobs = await _jobDataAccess.GetJobsByWorkflowIdAsync(workflowId, ct);
        if (jobs.Any(x => !IsTerminalStatus(x.Status))) return;

        var hasSuccessfulStep = jobs.Any(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase));
        var success = workflow.ExecutionMode.Equals("BestEffort", StringComparison.OrdinalIgnoreCase)
            ? hasSuccessfulStep
            : jobs.All(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase));

        workflow.Status = success ? "Completed" : "Failed";
        workflow.ModifiedDate = now;
        await _workflowDataAccess.UpdateWorkflowAsync(workflow.Id, workflow, ct);
        await InsertWorkflowHistoryIfMissingAsync(workflow, success, jobs.FirstOrDefault(x => !x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))?.StepNumber, success ? null : "Workflow finished with failed steps.", now, ct);
    }

    private async Task InsertJobHistoryAsync(Job job, bool success, bool executed, string? resultJson, string? failureCode, string? failureMessage, DateTime now, CancellationToken ct)
    {
        var command = await _jobDataAccess.GetCommandCatalogueByIdAsync(job.CommandCatalogueId, ct);
        var commandName = command?.Name?.Trim() ?? $"CommandCatalogue:{job.CommandCatalogueId}";

        await _jobHistoryDataAccess.InsertJobHistoryAsync(new JobHistory
        {
            JobId = job.Id,
            WorkflowId = job.WorkflowId,
            StepNumber = job.StepNumber,
            DeviceId = job.DeviceId,
            CommandCatalogueId = job.CommandCatalogueId,
            CommandName = commandName,
            PayloadJson = string.IsNullOrWhiteSpace(job.PayloadJson) ? "{}" : job.PayloadJson,
            ProviderType = job.ProviderType,
            ExecutionKind = NormalizeExecutionKind(command?.ExecutionKind),
            RollbackKind = NormalizeRollbackKind(command?.RollbackKind),
            Executed = executed,
            Success = success,
            ResultJson = resultJson,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            StartedAtUtc = executed ? job.ClaimedAtUtc : null,
            CompletedAtUtc = now,
            DurationMs = executed && job.ClaimedAtUtc.HasValue ? Math.Max(0, (int)Math.Round((now - job.ClaimedAtUtc.Value).TotalMilliseconds)) : null,
            RollbackOfJobHistoryId = job.RollbackOfJobHistoryId,
            CreatedDate = now
        }, ct);
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
            Executed = (await _jobDataAccess.GetJobsByWorkflowIdAsync(workflow.Id, ct)).Any(x => x.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)),
            Success = success,
            StartedAtUtc = workflow.ClaimedAtUtc,
            CompletedAtUtc = now,
            FailedStepNumber = failedStepNumber,
            FailureMessage = failureMessage,
            RollbackOfWorkflowHistoryId = workflow.RollbackOfWorkflowHistoryId,
            CreatedDate = now
        }, ct);
    }

    private async Task<GridPose> BuildCurrentPoseAsync(int deviceId, CancellationToken ct)
    {
        var status = await _deviceStatusDataAccess.GetDeviceStatusByDeviceIdAsync(deviceId, ct);
        return new GridPose
        {
            MapId = status?.PoseMapId ?? await _jobDataAccess.GetDeviceMapIdAsync(deviceId, ct),
            X = status?.GridX,
            Y = status?.GridY,
            Facing = status?.Facing,
            IsAligned = status?.IsGridAligned ?? false,
            IsTrusted = status?.IsGridPoseTrusted ?? false
        };
    }

    private async Task<(bool Ok, GridPose Pose, string? FailureMessage)> TryReadPlacePoseAsync(string payloadJson, int deviceId, CancellationToken ct)
    {
        var pose = new GridPose();
        string? failureMessage = null;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureMessage = "PLACE payload must be a JSON object.";
                return (false, pose, failureMessage);
            }

            var root = document.RootElement;
            var x = TryGetInt(root, "gridX", "GridX", "x", "X");
            var y = TryGetInt(root, "gridY", "GridY", "y", "Y");
            var facing = TryGetString(root, "facing", "Facing", "direction", "Direction");
            var mapId = TryGetInt(root, "poseMapId", "PoseMapId", "mapId", "MapId") ?? await _jobDataAccess.GetDeviceMapIdAsync(deviceId, ct);

            if (!x.HasValue || !y.HasValue || string.IsNullOrWhiteSpace(facing))
            {
                failureMessage = "PLACE requires gridX, gridY, and facing/direction.";
                return (false, pose, failureMessage);
            }

            if (!mapId.HasValue)
            {
                failureMessage = "PLACE requires the device to have an assigned map or a poseMapId payload value.";
                return (false, pose, failureMessage);
            }

            facing = NormalizeFacing(facing!);
            if (facing == null)
            {
                failureMessage = "Facing must be North, East, South, or West.";
                return (false, pose, failureMessage);
            }

            var map = await _mapDataAccess.GetMapByIdAsync(mapId.Value, ct);
            if (map == null || !map.IsActive)
            {
                failureMessage = "PLACE requires an active map.";
                return (false, pose, failureMessage);
            }

            if (!IsOnMap(map, x.Value, y.Value))
            {
                failureMessage = "PLACE coordinates are outside the map.";
                return (false, pose, failureMessage);
            }

            pose = new GridPose
            {
                MapId = mapId,
                X = x,
                Y = y,
                Facing = facing,
                IsAligned = true,
                IsTrusted = true
            };
            return (true, pose, failureMessage);
        }
        catch (JsonException)
        {
            failureMessage = "PLACE payload must be valid JSON.";
            return (false, pose, failureMessage);
        }
    }

    private async Task InvalidatePoseAsync(int deviceId, string reason, DateTime now, CancellationToken ct)
    {
        var status = await _deviceStatusDataAccess.GetDeviceStatusByDeviceIdAsync(deviceId, ct);
        if (status == null) return;
        status.IsGridAligned = false;
        status.IsGridPoseTrusted = false;
        status.PoseConfidence = 0;
        status.StatusMessage = reason;
        status.ModifiedDate = now;
        await _deviceStatusDataAccess.UpdateDeviceStatusAsync(status.Id, status, ct);
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

    private static void MoveOneCell(GridPose pose, int sign)
    {
        if (pose.Facing == "North") pose.Y += sign;
        else if (pose.Facing == "East") pose.X += sign;
        else if (pose.Facing == "South") pose.Y -= sign;
        else if (pose.Facing == "West") pose.X -= sign;
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

    private enum DispatchDecision
    {
        Claim,
        SkipAndContinue
    }

    private sealed class GridPose
    {
        public int? MapId { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public string? Facing { get; set; }
        public bool IsAligned { get; set; }
        public bool IsTrusted { get; set; }

        public GridPose Clone() => new()
        {
            MapId = MapId,
            X = X,
            Y = Y,
            Facing = Facing,
            IsAligned = IsAligned,
            IsTrusted = IsTrusted
        };
    }

    private sealed class ValidationResult
    {
        public bool IsValid { get; init; }
        public string FailureCode { get; init; } = string.Empty;
        public string FailureMessage { get; init; } = string.Empty;
        public GridPose? NextPose { get; init; }
        public Job? Job { get; set; }

        public static ValidationResult Valid(GridPose? nextPose = null) => new() { IsValid = true, NextPose = nextPose };
        public static ValidationResult Invalid(string failureCode, string failureMessage) => new() { IsValid = false, FailureCode = failureCode, FailureMessage = failureMessage };
    }
}
