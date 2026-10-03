namespace RobotControllerApi.BoundedContexts.Jobs.Dtos;

public class StartJobRequest
{
    // The job.claimedAtUtc value returned by claim-next, echoed back unchanged to identify the exact claim.
    public DateTime? ClaimedAtUtc { get; set; }
}
