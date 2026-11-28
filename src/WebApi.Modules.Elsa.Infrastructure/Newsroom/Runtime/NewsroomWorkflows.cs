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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

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
    public const string ContentId = "contentId";
    public const string ContentKind = "contentKind";

    /// <summary>The workflow property that keeps one of these values across resumes.</summary>
    public static string Property(string name) => "newsroom." + name;

    /// <summary>One correlation id per project, which its sub-workflows are to share.</summary>
    public static string CorrelationId(Guid projectId) => $"newsroom-project:{projectId:D}";
}

/// <summary>One content of a project's post, to be produced by a sub-workflow.</summary>
/// <param name="DefinitionId">The definition to run instead of the one bound to the content kind.</param>
public sealed record ContentWork(
    Guid ProjectId, int PostId, Guid SectionId, Guid ContentId, string ContentKind,
    IReadOnlyCollection<string> Categories, string? DefinitionId, string ParentInstanceId);

public interface IContentWorkflows
{
    /// <summary>Starts the content's sub-workflow unless it exists. Safe to call again.</summary>
    Task EnsureStartedAsync(ContentWork work, CancellationToken ct);
}

/// <summary>
/// Starts, resumes and cancels the newsroom workflows. Lives in the engine's container and is used through
/// <see cref="ElsaEngine.RunInTenantAsync{T}"/>, so the tenant is always set.
/// </summary>
internal sealed class NewsroomWorkflows(
    IWorkflowBindings bindings,
    IUnitRouter router,
    INewsRoomProjects projects,
    IWorkflowRuntime runtime,
    IWorkflowDefinitionService definitions,
    IWorkflowInstanceStore instances,
    IBookmarkStore bookmarks,
    IWorkflowResumer resumer,
    IStimulusHasher hasher,
    IDistributedLockProvider locks,
    ITenantAccessor tenant,
    IServiceScopeFactory scopes,
    ILogger<NewsroomWorkflows> logger) : IContentWorkflows
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The id of a project's main workflow instance, or of the sub-workflow of one of its contents. It is derived from
    /// the tenant and what the instance is for, so a second start, on any replica, finds the same instance.
    /// </summary>
    public static string InstanceId(string tenantId, Guid projectId, Guid? contentId = null)
    {
        var key = $"{tenantId.Length}:{tenantId}{projectId:D}" + (contentId is { } content ? $"/{content:D}" : "");
        return "nr" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..30];
    }

    /// <summary>
    /// Starts the project's workflow unless it exists. Returns the instance id, or null when none starts: the project
    /// is over, or no workflow is bound to its post kind (NewsRoom reports that on the project, and publishes
    /// <c>project-binding-available</c> once one is).
    /// </summary>
    public async Task<string?> StartProjectAsync(ProjectApproved project, CancellationToken ct)
    {
        var id = InstanceId(tenant.TenantId!, project.ProjectId);

        await using var handle = await LockAsync(id, ct);
        if (await FindAsync(id, ct) is not null)
        {
            logger.LogInformation("Project {ProjectId} already has workflow instance {InstanceId}.", project.ProjectId, id);
            return id;
        }

        // Events come in no particular order: the kill may have been delivered first, when there was nothing to cancel.
        if (await projects.GetAsync(project.ProjectId, ct) is { } current &&
            (ProjectStates.Is(current.State, ProjectStates.Killed) || ProjectStates.Is(current.State, ProjectStates.Published)))
        {
            logger.LogInformation("Project {ProjectId} is {State}; no workflow starts.", project.ProjectId, current.State);
            return null;
        }

        if (await bindings.ForProjectAsync(project.ProjectId, ct) is not { } choice)
        {
            logger.LogWarning("No workflow is bound to the post kind of project {ProjectId}; none starts until one is.",
                project.ProjectId);
            return null;
        }

        var input = new Dictionary<string, object>
        {
            [ProjectInput.ProjectId] = project.ProjectId.ToString("D"),
            [ProjectInput.PostId] = project.PostId.ToString(CultureInfo.InvariantCulture),
            [ProjectInput.UnitId] = project.SectionId.ToString("D")
        };
        if (project.PostKind is { } kind) input[ProjectInput.PostKind] = kind;

        await StartAsync(id, choice, project.ProjectId, input, parentId: null, ct);
        return id;
    }

    /// <inheritdoc />
    public async Task EnsureStartedAsync(ContentWork work, CancellationToken ct)
    {
        var id = InstanceId(tenant.TenantId!, work.ProjectId, work.ContentId);
        if (await FindAsync(id, ct) is not null) return;

        // The parent runs under its instance lock, which a cancellation takes too: no child starts after a cancel.
        await using var handle = await LockAsync(id, ct);
        if (await FindAsync(id, ct) is not null) return;

        // The unit that produces the kind, which is also where its binding is looked up.
        var unit = await router.RouteAsync(new RouteQuery(work.ContentKind, work.Categories), ct) ?? work.SectionId;
        var choice = work.DefinitionId is { Length: > 0 } given
            ? new WorkflowChoice(given)
            : await bindings.ForContentAsync(work.ContentKind, unit, ct)
              ?? new WorkflowChoice(DefaultWorkflows.Content);

        var input = new Dictionary<string, object>
        {
            [ProjectInput.ProjectId] = work.ProjectId.ToString("D"),
            [ProjectInput.PostId] = work.PostId.ToString(CultureInfo.InvariantCulture),
            [ProjectInput.UnitId] = unit.ToString("D"),
            [ProjectInput.ContentId] = work.ContentId.ToString("D"),
            [ProjectInput.ContentKind] = work.ContentKind
        };

        // In a scope of its own: the parent's run is still going on in this one.
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NewsroomWorkflows>()
            .StartAsync(id, choice, work.ProjectId, input, work.ParentInstanceId, ct);
    }

    private async Task StartAsync(
        string id, WorkflowChoice choice, Guid projectId, Dictionary<string, object> input, string? parentId,
        CancellationToken ct)
    {
        var version = choice.Version is { } number ? VersionOptions.SpecificVersion(number) : VersionOptions.Published;
        var handle = WorkflowDefinitionHandle.ByDefinitionId(choice.DefinitionId, version);
        if (await definitions.FindWorkflowGraphAsync(handle, ct) is null)
            throw new PlatformUnavailableException(
                $"Workflow '{choice.DefinitionId}' ({(choice.Version is { } v ? $"version {v}" : "published")}) is bound " +
                "but this tenant has no such definition.");

        var client = await runtime.CreateClientAsync(id, ct);
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = handle,
            CorrelationId = ProjectInput.CorrelationId(projectId),
            ParentId = parentId,
            Input = input,
            // The activities read the project from here after the first run too: input is not kept across resumes.
            Properties = input.ToDictionary(p => ProjectInput.Property(p.Key), p => p.Value)
        }, ct);
        if (response.CannotStart)
            throw new InvalidOperationException($"Workflow '{choice.DefinitionId}' refused to start for project {projectId}.");

        logger.LogInformation("Started workflow {DefinitionId} as {InstanceId} for project {ProjectId}: {Status}.",
            choice.DefinitionId, id, projectId, response.SubStatus);
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

    /// <summary>
    /// Cancels the project's workflows that still run: the main one first, which starts no more sub-workflows once it
    /// is cancelled, then every other instance of the project. A kill in NewsRoom is final.
    /// </summary>
    public async Task CancelProjectAsync(Guid projectId, CancellationToken ct)
    {
        var main = InstanceId(tenant.TenantId!, projectId);
        await using var handle = await LockAsync(main, ct); // not between a start's checks and its first run
        await CancelInstanceAsync(main, ct);

        var correlated = await instances.FindManyAsync(
            new WorkflowInstanceFilter { CorrelationId = ProjectInput.CorrelationId(projectId) }, ct);
        foreach (var instance in correlated.Where(i => i.Id != main))
            await CancelInstanceAsync(instance.Id, ct);
    }

    private async Task CancelInstanceAsync(string id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is null) return;

        // Elsa 3.9.0 cancels without the lock under which an instance runs, so a resume on another replica could save
        // over the cancellation; and it leaves the bookmarks in the store.
        await using (await locks.AcquireLockAsync($"workflow-instance:{id}", LockTimeout, ct))
        {
            if (await FindAsync(id, ct) is { Status: WorkflowStatus.Running })
            {
                var client = await runtime.CreateClientAsync(id, ct);
                await client.CancelAsync(ct);
                logger.LogInformation("Cancelled workflow instance {InstanceId}: its project was killed.", id);
            }

            await bookmarks.DeleteAsync(new BookmarkFilter { WorkflowInstanceId = id }, ct);
        }
    }

    // The instance lock cannot cover "does it exist, else create it": that takes a lock of its own.
    private ValueTask<IDistributedSynchronizationHandle> LockAsync(string instanceId, CancellationToken ct) =>
        locks.AcquireLockAsync($"newsroom-start:{instanceId}", LockTimeout, ct);

    private ValueTask<WorkflowInstance?> FindAsync(string instanceId, CancellationToken ct) =>
        instances.FindAsync(new WorkflowInstanceFilter { Id = instanceId }, ct);
}
