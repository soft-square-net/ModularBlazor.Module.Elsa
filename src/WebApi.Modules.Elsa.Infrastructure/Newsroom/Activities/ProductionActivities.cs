using System.Globalization;
using System.Net;
using System.Text.Json;
using Elsa.Common.Multitenancy;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Gives the content to someone: waits for the unit's manager, then picks the least loaded holder of the role who is checked in to a running shift.",
    DisplayName = "Assign in shift")]
[FlowNode(Assigned)]
public sealed class AssignInShift : NewsroomActivity
{
    public const string Assigned = "Assigned";

    public Input<string?> ContentId { get; set; } = default!;

    /// <summary>The unit whose shift does the work; the content's unit when not set.</summary>
    public Input<string?> UnitId { get; set; } = default!;

    public Input<string> RoleKey { get; set; } = default!;

    /// <summary>How long a manager has to assign by hand first.</summary>
    public Input<TimeSpan?> ManagerDelay { get; set; } = default!;

    public Output<string?> AssigneeId { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        const string AssignAfter = "assignAfter";
        var api = context.GetRequiredService<ContentModuleApi>();
        var contentId = RequiredId(context, ContentId, nameof(ContentId));
        var role = Value(context, RoleKey) is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException("The activity needs a RoleKey.");

        // Counted from the first look, and kept, so that a restart does not give the manager the delay again.
        if (context.GetProperty<string>(AssignAfter) is not { } stored)
        {
            stored = (Now(context) + (Value(context, ManagerDelay) ?? TimeSpan.Zero)).ToString("O", CultureInfo.InvariantCulture);
            context.SetProperty(AssignAfter, stored);
        }

        var assignAfter = DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture);
        var content = await api.ContentAsync(contentId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve content {contentId}.");
        if (!string.IsNullOrEmpty(content.AssigneeId)) return Done(content.AssigneeId);
        if (Now(context) < assignAfter) return WaitStep.For(Subjects.Content(contentId)).Until(assignAfter);

        var unitId = UnitInput(context, UnitId) ?? content.UnitId ?? RememberedUnit(context)
            ?? throw new InvalidOperationException($"Content {contentId} has no unit whose shift could take it.");
        var wait = WaitStep.For(Subjects.Content(contentId), Subjects.Unit(unitId)).Until(Soon(context));

        var shifts = await context.GetRequiredService<ShiftsModuleApi>().RunningAsync(unitId, ct);
        var candidates = shifts.SelectMany(s => s.Rota)
            .Where(m => string.Equals(m.RoleKey, role, StringComparison.OrdinalIgnoreCase) &&
                        m is { CheckedInAt: not null, CheckedOutAt: null })
            .Select(m => m.UserId).Distinct().ToArray();
        if (candidates.Length == 0) return wait;

        var workload = context.GetRequiredService<IShiftWorkload>();
        var loads = new List<(int Load, string UserId)>();
        foreach (var userId in candidates) loads.Add((await workload.OpenItemsAsync(userId, ct), userId));
        var chosen = loads.OrderBy(l => l.Load).ThenBy(l => l.UserId, StringComparer.Ordinal).First().UserId;

        var answer = await api.AssignAsync(content, chosen, ct);
        if (answer.IsSuccess) return Done(chosen);

        // A conflict is usually a change made meanwhile; anything else is logged and tried again, never a fault.
        Log(context).LogRefusedAssignment(contentId, (int)answer.Status, answer.Reason());
        return wait;

        WaitStep Done(string assignee)
        {
            context.Set(AssigneeId, assignee);
            return WaitStep.Done(Assigned);
        }
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom", "Waits until the content reaches one of the stages.", DisplayName = "Wait for stage")]
[FlowNode(Reached)]
public sealed class WaitForStage : NewsroomActivity
{
    public const string Reached = "Reached";
    private const string Accepted = "accepted";

    public Input<string?> ContentId { get; set; } = default!;

    /// <summary>Content's stage keys; "accepted" when not set.</summary>
    public Input<ICollection<string>?> Stages { get; set; } = default!;

    public Output<string?> Stage { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var contentId = RequiredId(context, ContentId, nameof(ContentId));
        var content = await context.GetRequiredService<ContentModuleApi>().ContentAsync(contentId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve content {contentId}.");
        if (!Includes(Value(context, Stages), content.Stage)) return WaitStep.For(Subjects.Content(contentId));

        context.Set(Stage, content.Stage);
        return WaitStep.Done(Reached);
    }

    internal static bool Includes(ICollection<string>? wanted, string stage) =>
        wanted is { Count: > 0 }
            ? wanted.Contains(stage, StringComparer.OrdinalIgnoreCase)
            : string.Equals(stage, Accepted, StringComparison.OrdinalIgnoreCase);
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Waits for the content to reach a stage by a deadline; when it does not, tells the shift's lead, or else the unit's manager.",
    DisplayName = "Deadline")]
[FlowNode(Met, Escalated)]
public sealed class Deadline : NewsroomActivity
{
    public const string Met = "Met";
    public const string Escalated = "Escalated";
    public const string MissedNotification = "workflow.deadline-missed";
    private const string ManagerPosition = "manager";

    public Input<string?> ContentId { get; set; } = default!;
    public Input<ICollection<string>?> Stages { get; set; } = default!;

    /// <summary>The deadline.</summary>
    public Input<DateTimeOffset?> Due { get; set; } = default!;

    /// <summary>Instead of <see cref="Due"/>: this long after the activity starts.</summary>
    public Input<TimeSpan?> DueIn { get; set; } = default!;

    public Output<ICollection<string>?> EscalatedTo { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        const string DueProperty = "due";
        var contentId = RequiredId(context, ContentId, nameof(ContentId));
        if (context.GetProperty<string>(DueProperty) is not { } stored)
        {
            var computed = Value(context, Due) ?? (Value(context, DueIn) is { } within
                ? Now(context) + within
                : throw new InvalidOperationException("The activity needs Due or DueIn."));
            stored = computed.ToString("O", CultureInfo.InvariantCulture);
            context.SetProperty(DueProperty, stored);
        }

        var due = DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture);
        var content = await context.GetRequiredService<ContentModuleApi>().ContentAsync(contentId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve content {contentId}.");
        if (WaitForStage.Includes(Value(context, Stages), content.Stage)) return WaitStep.Done(Met);
        if (Now(context) < due) return WaitStep.For(Subjects.Content(contentId)).Until(due);

        var unitId = content.UnitId ?? RememberedUnit(context);
        context.Set(EscalatedTo, await EscalateAsync(context, unitId, new
        {
            contentId, due, stage = content.Stage, assigneeId = content.AssigneeId,
            projectId = Remembered(context, "projectId")
        }, ct));
        return WaitStep.Done(Escalated);
    }

    private static async Task<ICollection<string>> EscalateAsync(
        ActivityExecutionContext context, Guid? unitId, object payload, CancellationToken ct)
    {
        if (unitId is not { } unit)
        {
            Log(context).LogNobodyToTell(context.WorkflowExecutionContext.Id);
            return [];
        }

        var people = (await context.GetRequiredService<ShiftsModuleApi>().RunningAsync(unit, ct))
            .SelectMany(s => s.Rota).Where(m => m.IsLead).Select(m => m.UserId).Distinct().ToList();

        // No lead on shift: the managers of the unit, or of the nearest unit above it that has one.
        var organization = context.GetRequiredService<OrganizationModuleApi>();
        var snapshot = await organization.UnitAsync(unit, ct);
        for (var current = snapshot; people.Count == 0 && current is not null;
             current = current.ParentId is { } parent ? await organization.UnitAsync(parent, ct) : null)
            people.AddRange((await organization.MembersAsync(current.Id, ct))
                .Where(m => string.Equals(m.PositionKey, ManagerPosition, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Subject).Distinct());

        var messenger = context.GetRequiredService<ILiveMessenger>();
        var tenant = Notify.TenantOf(context);
        var data = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
        foreach (var person in people)
            await messenger.ToUserAsync(tenant, person, MissedNotification, data, ct);

        if (people.Count == 0)
        {
            if (snapshot is null) Log(context).LogNobodyToTell(context.WorkflowExecutionContext.Id);
            else await messenger.ToUnitAsync(tenant, snapshot.Path, MissedNotification, data, ct);
        }

        return people;
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Sends the project to the desk and waits until the desk approves the post or returns it.", DisplayName = "Desk review")]
[FlowNode(Approved, Returned)]
public sealed class DeskReview : NewsroomActivity
{
    public const string Approved = "Approved";
    public const string Returned = "Returned";
    private const string ReturnedVersion = "returnedVersion";

    public Input<string?> ProjectId { get; set; } = default!;
    public Input<string?> PostId { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var projects = context.GetRequiredService<INewsRoomProjects>();
        var projectId = RequiredProject(context, ProjectId);
        var postId = RequiredPost(context, PostId);
        var wait = WaitStep.For(Subjects.Project(projectId), Subjects.Post(postId));

        var post = await context.GetRequiredService<ContentModuleApi>().PostAsync(postId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve post {postId}.");
        if (post.State is "Approved" or "Scheduled" or "Published") return WaitStep.Done(Approved);

        var project = await ProjectAsync(context, projectId, ct);

        // Content keeps the post "Returned" until it is submitted again: a version newer than the one acted on before.
        var actedOn = int.TryParse(Remembered(context, ReturnedVersion), out var version) ? version : -1;
        if (post.State == "Returned" && post.Version > actedOn)
        {
            if (ProjectStates.Is(project.State, ProjectStates.ReadyForDesk) &&
                !await projects.SetStateAsync(projectId, ProjectStates.InProduction, ApprovalOf(context, project), null, ct))
                return wait.Until(Soon(context));

            Remember(context, ReturnedVersion, post.Version.ToString(CultureInfo.InvariantCulture));
            return WaitStep.Done(Returned);
        }

        if (ProjectStates.Is(project.State, ProjectStates.InProduction) &&
            !await projects.SetStateAsync(projectId, ProjectStates.ReadyForDesk, ApprovalOf(context, project), null, ct))
            return wait.Until(Soon(context));

        return wait;
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom", "Publishes the post, now or at a set time.", DisplayName = "Publish post")]
[FlowNode(Published, Refused)]
public sealed class PublishPost : NewsroomActivity
{
    public const string Published = "Published";
    public const string Refused = "Refused";

    public Input<string?> ProjectId { get; set; } = default!;
    public Input<string?> PostId { get; set; } = default!;
    public Input<DateTimeOffset?> PublishAt { get; set; } = default!;
    public Output<string?> Reason { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var content = context.GetRequiredService<ContentModuleApi>();
        var projects = context.GetRequiredService<INewsRoomProjects>();
        var projectId = RequiredProject(context, ProjectId);
        var postId = RequiredPost(context, PostId);
        var wait = WaitStep.For(Subjects.Project(projectId), Subjects.Post(postId));

        var post = await content.PostAsync(postId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve post {postId}.");
        if (post.State == "Published")
        {
            await ReportAsync(ProjectStates.Published);
            return WaitStep.Done(Published);
        }

        // Nothing is published for a project that was killed or is back with the moderator.
        var project = await ProjectAsync(context, projectId, ct);
        if (ProjectStates.Is(project.State, ProjectStates.Killed) ||
            ProjectStates.Is(project.State, ProjectStates.NeedsModeration)) return wait;

        if (post.State == "Scheduled") return await ScheduledAsync(post.ScheduledFor);

        var publishAt = Value(context, PublishAt);
        var later = publishAt > Now(context);
        var answer = later
            ? await content.ScheduleAsync(postId, post.Version, publishAt!.Value, ct)
            : await content.PublishAsync(postId, post.Version, ct);

        if (answer.IsSuccess)
        {
            if (later) return await ScheduledAsync(publishAt);
            await ReportAsync(ProjectStates.Published);
            return WaitStep.Done(Published);
        }

        if (answer.Status == HttpStatusCode.Conflict && (await content.PostAsync(postId, ct))?.Version != post.Version)
            return wait.Until(Soon(context));

        context.Set(Reason, answer.Reason());
        return WaitStep.Done(Refused);

        async Task<WaitStep> ScheduledAsync(DateTimeOffset? at)
        {
            await ReportAsync(ProjectStates.Scheduled);
            // Content publishes it; look shortly after the time it should.
            return at is { } time && time > Now(context) ? wait.Until(time.AddSeconds(30)) : wait.Until(Soon(context));
        }

        // NewsRoom follows Content's own events too, so a refused report is nothing to wait for.
        async Task ReportAsync(string state)
        {
            var current = await ProjectAsync(context, projectId, ct);
            if (!ProjectStates.Is(current.State, state))
                await projects.SetStateAsync(projectId, state, ApprovalOf(context, current), null, ct);
        }
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Sends a live notification to a person, or to a unit and the units above it.", DisplayName = "Notify")]
[FlowNode(Sent)]
public sealed class Notify : NewsroomActivity
{
    public const string Sent = "Done";
    private const string DefaultType = "workflow.notification";

    public Input<string?> UserId { get; set; } = default!;

    /// <summary>The unit's path; used when no person is set.</summary>
    public Input<string?> UnitPath { get; set; } = default!;

    public Input<string?> NotificationType { get; set; } = default!;
    public Input<string?> Message { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var user = Value(context, UserId);
        var unit = Value(context, UnitPath);
        if (string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(unit))
            throw new InvalidOperationException("The activity needs a UserId or a UnitPath.");

        var type = Value(context, NotificationType) is { Length: > 0 } given ? given : DefaultType;
        var payload = JsonSerializer.SerializeToElement(new
        {
            message = Value(context, Message),
            projectId = Remembered(context, "projectId"),
            postId = Remembered(context, "postId")
        }, JsonSerializerOptions.Web);

        var messenger = context.GetRequiredService<ILiveMessenger>();
        if (!string.IsNullOrWhiteSpace(user))
            await messenger.ToUserAsync(TenantOf(context), user, type, payload, ct);
        else
            await messenger.ToUnitAsync(TenantOf(context), unit!, type, payload, ct);

        return WaitStep.Done(Sent);
    }

    internal static string TenantOf(ActivityExecutionContext context) =>
        context.GetRequiredService<ITenantAccessor>().TenantId is { Length: > 0 } tenant
            ? tenant
            : throw new InvalidOperationException("The workflow runs without a tenant.");
}
