namespace RobotControllerApi.BoundedContexts.Jobs.Dtos;

public class FailJobRequest
{
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
}
