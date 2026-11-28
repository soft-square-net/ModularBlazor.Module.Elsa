using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Elsa.Common.Models;
using Elsa.Common.Multitenancy;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

/// <summary>A signal bookmark's payload: the subject an activity waits to hear about (see <see cref="Subjects"/>).</summary>
public sealed record NewsroomSignal(string Subject)
{
    public const string BookmarkName = "Newsroom.Signal";
}

/// <summary>The project a workflow instance runs for; the activities' inputs default to it.</summary>
public static class ProjectInput
{
    public const string ProjectId = "projectId";
    public const string PostId = "postId";
    public const string UnitId = "unitId";
    public const string PostKind = "postKind";

    /// <summary>The workflow property that keeps one of these values across resumes.</summary>
    public static string Property(string name) => "newsroom." + name;

    /// <summary>One correlation id per project, which its sub-workflows are to share.</summary>
    public static string CorrelationId(Guid projectId) => $"newsroom-project:{projectId:D}";
}

/// <summary>
/// Starts, resumes and cancels the newsroom workflows. Lives in the engine's container and is used through
/// <see cref="ElsaEngine.RunInTenantAsync{T}"/>, so the tenant is always set.
/// </summary>
internal sealed class NewsroomWorkflows(
    IWorkflowBindings bindings,
    INewsRoomProjects projects,
    IWorkflowRuntime runtime,
    IWorkflowInstanceStore instances,
    IBookmarkStore bookmarks,
    IWorkflowResumer resumer,
    IStimulusHasher hasher,
    IDistributedLockProvider locks,
    ITenantAccessor tenant,
    ILogger<NewsroomWorkflows> logger)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The id of a project's main workflow instance. It is derived from the tenant and the project, so a second delivery
    /// of the approval, on any replica, finds the same instance.
    /// </summary>
    public static string InstanceId(string tenantId, Guid projectId) =>
        "nr" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId.Length}:{tenantId}{projectId:D}")))[..30];

    /// <summary>
    /// Starts the project's workflow unless it exists. Returns the instance id, or null when the project was killed
    /// before its workflow started.
    /// </summary>
    public async Task<string?> StartProjectAsync(ProjectApproved project, CancellationToken ct)
    {
        var definitionId = await bindings.ResolveAsync(project, ct)
            ?? throw new InvalidOperationException(
                $"No workflow is bound for project {project.ProjectId} ({SettingWorkflowBindings.Setting} is not set): nothing was started.");
        var id = InstanceId(tenant.TenantId!, project.ProjectId);

        await using var handle = await LockAsync(id, ct);
        if (await FindAsync(id, ct) is not null)
        {
            logger.LogInformation("Project {ProjectId} already has workflow instance {InstanceId}.", project.ProjectId, id);
            return id;
        }

        // Events come in no particular order: the kill may have been delivered first, when there was nothing to cancel.
        if (await projects.GetAsync(project.ProjectId, ct) is { } current && ProjectStates.Is(current.State, ProjectStates.Killed))
        {
            logger.LogInformation("Project {ProjectId} was killed; no workflow starts.", project.ProjectId);
            return null;
        }

        var input = new Dictionary<string, object>
        {
            [ProjectInput.ProjectId] = project.ProjectId.ToString("D"),
            [ProjectInput.PostId] = project.PostId.ToString(CultureInfo.InvariantCulture),
            [ProjectInput.UnitId] = project.SectionId.ToString("D")
        };
        if (project.PostKind is { } kind) input[ProjectInput.PostKind] = kind;

        var client = await runtime.CreateClientAsync(id, ct);
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(definitionId, VersionOptions.Published),
            CorrelationId = ProjectInput.CorrelationId(project.ProjectId),
            Input = input,
            // The activities read the project from here after the first run too: input is not kept across resumes.
            Properties = input.ToDictionary(p => ProjectInput.Property(p.Key), p => p.Value)
        }, ct);
        if (response.CannotStart)
            throw new InvalidOperationException($"Workflow '{definitionId}' refused to start for project {project.ProjectId}.");

        logger.LogInformation("Started workflow {DefinitionId} as {InstanceId} for project {ProjectId}: {Status}.",
            definitionId, id, project.ProjectId, response.SubStatus);
        return id;
    }

    /// <summary>Wakes every activity of the tenant that waits on the subject. Returns how many runs it made.</summary>
    public async Task<int> SignalAsync(string subject, CancellationToken ct)
    {
        var responses = await resumer.ResumeAsync(new BookmarkFilter
        {
            Name = NewsroomSignal.BookmarkName,
            Hash = hasher.Hash(NewsroomSignal.BookmarkName, new NewsroomSignal(subject))
        }, cancellationToken: ct);
        return responses.Count();
    }

    /// <summary>Cancels the project's workflow, when it has one that still runs. A kill in NewsRoom is final.</summary>
    public async Task CancelProjectAsync(Guid projectId, CancellationToken ct)
    {
        var id = InstanceId(tenant.TenantId!, projectId);
        await using var handle = await LockAsync(id, ct); // not between a start's checks and its first run
        if (await FindAsync(id, ct) is not { } instance) return;
        if (instance.Status != WorkflowStatus.Running)
        {
            await bookmarks.DeleteAsync(new BookmarkFilter { WorkflowInstanceId = id }, ct); // a delivery that stopped half way
            return;
        }

        // Elsa 3.9.0 cancels without the lock under which an instance runs, so a resume on another replica could save
        // over the cancellation; and it leaves the bookmarks in the store.
        await using (await locks.AcquireLockAsync($"workflow-instance:{id}", LockTimeout, ct))
        {
            var client = await runtime.CreateClientAsync(id, ct);
            await client.CancelAsync(ct);
            await bookmarks.DeleteAsync(new BookmarkFilter { WorkflowInstanceId = id }, ct);
        }

        logger.LogInformation("Cancelled workflow instance {InstanceId}: project {ProjectId} was killed.", id, projectId);
    }

    // The instance lock cannot cover "does it exist, else create it": that takes a lock of its own.
    private ValueTask<IDistributedSynchronizationHandle> LockAsync(string instanceId, CancellationToken ct) =>
        locks.AcquireLockAsync($"newsroom-start:{instanceId}", LockTimeout, ct);

    private ValueTask<WorkflowInstance?> FindAsync(string instanceId, CancellationToken ct) =>
        instances.FindAsync(new WorkflowInstanceFilter { Id = instanceId }, ct);
}
