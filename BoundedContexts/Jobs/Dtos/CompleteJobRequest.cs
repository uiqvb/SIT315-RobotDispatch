namespace RobotControllerApi.BoundedContexts.Jobs.Dtos;

public class CompleteJobRequest
{
    public string? ResultJson { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
}
