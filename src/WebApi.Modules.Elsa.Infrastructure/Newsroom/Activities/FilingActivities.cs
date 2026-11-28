using System.Net;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

[Activity("SoftSquare.Newsroom", "Newsroom", "Finds the unit that produces a content kind, by NewsRoom's routing rules.", DisplayName = "Route to unit")]
[FlowNode(Routed, NoRoute)]
public sealed class RouteToUnit : NewsroomActivity
{
    public const string Routed = "Routed";
    public const string NoRoute = "NoRoute";

    public Input<string> ContentKind { get; set; } = default!;
    public Input<ICollection<string>?> Categories { get; set; } = default!;

    public Output<string?> UnitId { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var unit = await context.GetRequiredService<IUnitRouter>().RouteAsync(new RouteQuery(
            Value(context, ContentKind) ?? "",
            Value(context, Categories)?.ToArray() ?? []), ct);
        context.Set(UnitId, unit?.ToString("D"));
        return WaitStep.Done(unit is null ? NoRoute : Routed);
    }
}

/// <summary>What the check of a post's filing saw, to tell later that something changed.</summary>
internal static class Filing
{
    public const string Property = "filing";

    public static string Of(IEnumerable<string> categories, Guid? postUnit, Guid? section) =>
        $"{string.Join(',', categories.Order(StringComparer.Ordinal))}|{postUnit:N}|{section:N}";

    /// <summary>
    /// Whether the filing must be checked again: a moderator approved the project again, or the post's categories or
    /// unit, or the project's Section, are not what the check saw.
    /// </summary>
    /// <param name="checkedAs">What the check saw; null when nothing was checked.</param>
    public static bool Changed(
        string? checkedAs, int checkedApproval, ProjectSnapshot project, PostSnapshot post) =>
        project.ApprovalNumber > checkedApproval ||
        (checkedAs is not null && checkedAs != Of(post.Categories, post.UnitId, project.SectionId));
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Files the project's post in its Section when the Section serves the post's categories; otherwise sends the project back to moderation.",
    DisplayName = "Ensure post in unit categories")]
[FlowNode(Filed, NeedsModeration)]
public sealed class EnsurePostInUnitCategories : NewsroomActivity
{
    public const string Filed = "Filed";
    public const string NeedsModeration = "NeedsModeration";

    public Input<string?> ProjectId { get; set; } = default!;
    public Input<string?> PostId { get; set; } = default!;

    /// <summary>The unit to file the post in; the project's Section when not set.</summary>
    public Input<string?> UnitId { get; set; } = default!;

    public Output<string?> Reason { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var projects = context.GetRequiredService<INewsRoomProjects>();
        var content = context.GetRequiredService<ContentModuleApi>();
        var projectId = RequiredProject(context, ProjectId);
        var postId = RequiredPost(context, PostId);
        var project = await ProjectAsync(context, projectId, ct);
        var retry = WaitStep.For(Subjects.Project(projectId), Subjects.Post(postId)).Until(Soon(context));

        if (ProjectStates.Is(project.State, ProjectStates.Killed))
            return WaitStep.For(Subjects.Project(projectId)); // the kill's event cancels the workflow
        RememberApproval(context, project.ApprovalNumber);

        var post = await content.PostAsync(postId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve post {postId}.");
        if (ProjectStates.Is(project.State, ProjectStates.NeedsModeration))
            return Ended(NeedsModeration, post.UnitId); // reported before; the moderator has not answered yet

        var unitId = UnitInput(context, UnitId) ?? project.SectionId ?? RememberedUnit(context)
            ?? throw new InvalidOperationException($"Project {projectId} has no Section to file its post in.");
        var served = await context.GetRequiredService<OrganizationModuleApi>().CategoriesAsync(unitId, ct);
        if (served is null) return await SendBackAsync($"The Section {unitId} is not known to Organization.");
        if (!UnitCategories.Covers(served, post.Categories))
            return await SendBackAsync("None of the post's categories belongs to the Section.");

        if (post.UnitId != unitId)
        {
            var answer = await content.SetPostUnitAsync(postId, post.Version, unitId, ct);
            if (!answer.IsSuccess)
            {
                // A newer version means the post changed meanwhile: look again. Otherwise Content refused the unit.
                if (answer.Status == HttpStatusCode.Conflict &&
                    (await content.PostAsync(postId, ct))?.Version != post.Version) return retry;
                return await SendBackAsync($"Content refused to file the post in the Section: {answer.Reason()}");
            }
        }

        if (ProjectStates.Is(project.State, ProjectStates.Approved) &&
            !await projects.SetStateAsync(projectId, ProjectStates.InProduction, project.ApprovalNumber, null, ct))
            return retry;

        return Ended(Filed, unitId);

        async Task<WaitStep> SendBackAsync(string reason)
        {
            // Content refuses categories that the post's unit does not serve, so the moderator needs the unit cleared.
            if (post.UnitId is not null)
            {
                var cleared = await content.SetPostUnitAsync(postId, post.Version, null, ct);
                if (cleared.IsSuccess) post = post with { UnitId = null };
                else if (cleared.Status == HttpStatusCode.Conflict &&
                         (await content.PostAsync(postId, ct))?.Version != post.Version) return retry;
                else Log(context).LogRefusedClear(postId, cleared.Reason());
            }

            if (!await projects.SetStateAsync(projectId, ProjectStates.NeedsModeration, project.ApprovalNumber, reason, ct))
                return retry;

            context.Set(Reason, reason);
            return Ended(NeedsModeration, post.UnitId);
        }

        WaitStep Ended(string outcome, Guid? postUnit)
        {
            Remember(context, Filing.Property, Filing.Of(post.Categories, postUnit, project.SectionId));
            return WaitStep.Done(outcome);
        }
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Waits until the post's filing must be checked again: its categories or unit changed, or a moderator approved the project again.",
    DisplayName = "Wait for refiling")]
[FlowNode(Refiled, ProjectEnded)]
public sealed class WaitForRefiling : NewsroomActivity
{
    public const string Refiled = "Refiled";

    public Input<string?> ProjectId { get; set; } = default!;
    public Input<string?> PostId { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var projectId = RequiredProject(context, ProjectId);
        var postId = RequiredPost(context, PostId);
        var wait = WaitStep.For(Subjects.Project(projectId), Subjects.Post(postId));
        var project = await ProjectAsync(context, projectId, ct);

        if (EndOf(project) is { } end) return end;

        // While the moderator has not answered, their changes to the post are not for the workflow to act on.
        if (ProjectStates.Is(project.State, ProjectStates.NeedsModeration)) return wait;
        if (project.ApprovalNumber > ApprovalOf(context, project)) return WaitStep.Done(Refiled);

        var post = await context.GetRequiredService<ContentModuleApi>().PostAsync(postId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve post {postId}.");
        var checkedAs = Remembered(context, Filing.Property);
        return checkedAs is null || Filing.Changed(checkedAs, ApprovalOf(context, project), project, post)
            ? WaitStep.Done(Refiled)
            : wait;
    }
}
