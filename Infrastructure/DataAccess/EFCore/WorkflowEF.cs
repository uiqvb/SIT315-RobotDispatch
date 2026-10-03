using Microsoft.EntityFrameworkCore;
using Npgsql;
using RobotControllerApi.BoundedContexts.Workflows.Models;
using RobotControllerApi.BoundedContexts.Workflows.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class WorkflowEF : EfDataAccessBase, IWorkflowDataAccess
{
    public WorkflowEF(RobotContext context) : base(context) { }

    public List<Workflow> GetWorkflows() => Context.Workflows.OrderBy(x => x.Id).ToList();

    public Workflow? GetWorkflowById(int id) => Context.Workflows.Find(id);

    public List<Workflow> GetWorkflowsByDeviceId(int deviceId) => Context.Workflows.Where(x => x.DeviceId == deviceId).OrderBy(x => x.Id).ToList();

    public Workflow? GetOldestQueuedWorkflowByDeviceId(int deviceId) => Context.Workflows.Where(x => x.DeviceId == deviceId && x.Status == "Queued").OrderBy(x => x.CreatedDate).ThenBy(x => x.Id).FirstOrDefault();

    public Workflow InsertWorkflow(Workflow newWorkflow)
    {
        Context.Workflows.Add(newWorkflow);
        Context.SaveChanges();
        return newWorkflow;
    }

    public bool UpdateWorkflow(int id, Workflow updatedWorkflow)
    {
        var existing = Context.Workflows.Find(id);
        if (existing == null) return false;

        existing.DeviceId = updatedWorkflow.DeviceId;
        existing.Name = updatedWorkflow.Name;
        existing.Description = updatedWorkflow.Description;
        existing.SchemaVersion = updatedWorkflow.SchemaVersion;
        existing.ExecutionMode = updatedWorkflow.ExecutionMode;
        existing.ProviderType = updatedWorkflow.ProviderType;
        existing.Status = updatedWorkflow.Status;
        existing.RequestedByAppUserId = updatedWorkflow.RequestedByAppUserId;
        existing.ClaimedByDeviceCredentialId = updatedWorkflow.ClaimedByDeviceCredentialId;
        existing.ClaimedAtUtc = updatedWorkflow.ClaimedAtUtc;
        existing.LeaseExpiresAtUtc = updatedWorkflow.LeaseExpiresAtUtc;
        existing.IsRollback = updatedWorkflow.IsRollback;
        existing.RollbackOfWorkflowHistoryId = updatedWorkflow.RollbackOfWorkflowHistoryId;
        existing.ModifiedDate = updatedWorkflow.ModifiedDate;

        return Context.SaveChanges() > 0;
    }

    public bool DeleteWorkflow(int id)
    {
        var existing = Context.Workflows.Find(id);
        if (existing == null) return false;

        Context.Workflows.Remove(existing);
        return Context.SaveChanges() > 0;
    }

    public bool DeviceExistsAndActive(int deviceId) => Exists("SELECT EXISTS (SELECT 1 FROM public.device WHERE id = @Id AND isactive = true)", new[] { new NpgsqlParameter("@Id", deviceId) });

    public bool DeviceCredentialOwnsDevice(int deviceCredentialId, int deviceId) => Exists("SELECT EXISTS (SELECT 1 FROM public.devicecredential dc JOIN public.device d ON d.id = dc.deviceid WHERE dc.id = @CredentialId AND dc.deviceid = @DeviceId AND dc.isactive = true AND dc.revokedatutc IS NULL AND (dc.expiresatutc IS NULL OR dc.expiresatutc > (now() AT TIME ZONE 'utc')) AND d.isactive = true)", new[] { new NpgsqlParameter("@CredentialId", deviceCredentialId), new NpgsqlParameter("@DeviceId", deviceId) });

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Workflow?> GetWorkflowByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetWorkflowById(id));
    public Task<bool> UpdateWorkflowAsync(int id, Workflow updatedWorkflow, CancellationToken ct = default) => Task.FromResult(UpdateWorkflow(id, updatedWorkflow));
}
