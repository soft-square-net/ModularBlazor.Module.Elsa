using System.Globalization;
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

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Gives a content to the unit that produces its kind. Without a route, or when Content refuses, the content keeps its unit.",
    DisplayName = "File content in unit")]
[FlowNode(Filed, Kept, ProjectEnded)]
public sealed class FileContentInUnit : NewsroomActivity
{
    public const string Filed = "Filed";
    public const string Kept = "Kept";

    public Input<string?> ContentId { get; set; } = default!;

    /// <summary>The content kind to route; the content's own when not set.</summary>
    public Input<string?> ContentKind { get; set; } = default!;

    /// <summary>The unit to give the content to, instead of routing it.</summary>
    public Input<string?> UnitId { get; set; } = default!;

    /// <summary>The unit the content is in afterwards.</summary>
    public Output<string?> FiledIn { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var api = context.GetRequiredService<ContentModuleApi>();
        var contentId = RequiredContent(context, ContentId);
        if (await ProjectEndAsync(context, ct) is { } end) return end;

        var content = await api.ContentAsync(contentId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve content {contentId}.");
        var retry = WaitOn(context, Subjects.Content(contentId)).Until(Soon(context));

        var section = RememberedUnit(context);
        var kind = Value(context, ContentKind) is { Length: > 0 } given
            ? given
            : content.FieldKey ?? Remembered(context, ProjectInput.ContentKind) ?? "";
        var unit = UnitInput(context, UnitId)
            ?? await context.GetRequiredService<IUnitRouter>().RouteAsync(new RouteQuery(kind, []), ct)
            ?? section;
        if (unit is not { } target || content.UnitId == target) return End(Filed, content.UnitId);

        var answer = await api.SetContentUnitAsync(contentId, content.Version, target, ct);
        if (answer.IsSuccess) return End(Filed, target);
        if (answer.Status == HttpStatusCode.Conflict && (await api.ContentAsync(contentId, ct))?.Version != content.Version)
            return retry;

        Log(context).LogRefusedContentUnit(contentId, target, (int)answer.Status, answer.Reason());
        return End(Kept, content.UnitId);

        WaitStep End(string outcome, Guid? filedIn)
        {
            context.Set(FiledIn, filedIn?.ToString("D"));
            // The activities after this one work in the content's unit.
            if (filedIn is { } now) Remember(context, ProjectInput.UnitId, now.ToString("D"));
            return WaitStep.Done(outcome);
        }
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Gets a content produced by the people on shift: assigns it, hands it to the next shift when its holder's shift ends, and ends when it reaches one of the stages.",
    DisplayName = "Work in shift")]
[FlowNode(Reached, ProjectEnded)]
public sealed class WorkInShift : NewsroomActivity
{
    public const string Reached = "Reached";
    private const string HolderProperty = "holder";
    private const string ShiftProperty = "heldInShift";

    public Input<string?> ContentId { get; set; } = default!;

    /// <summary>The unit whose shifts do the work; the content's unit when not set.</summary>
    public Input<string?> UnitId { get; set; } = default!;

    public Input<string> RoleKey { get; set; } = default!;

    /// <summary>How long a manager has to assign by hand first.</summary>
    public Input<TimeSpan?> ManagerDelay { get; set; } = default!;

    /// <summary>Content's stage keys that end the work; "accepted" when not set.</summary>
    public Input<ICollection<string>?> Stages { get; set; } = default!;

    public Output<string?> AssigneeId { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var api = context.GetRequiredService<ContentModuleApi>();
        var shifts = context.GetRequiredService<ShiftsModuleApi>();
        var contentId = RequiredContent(context, ContentId);
        var role = Value(context, RoleKey) is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException("The activity needs a RoleKey.");
        var assignAfter = KeptTime(context, "assignAfter", () => Now(context) + (Value(context, ManagerDelay) ?? TimeSpan.Zero));
        if (await ProjectEndAsync(context, ct) is { } end) return end;

        var content = await api.ContentAsync(contentId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve content {contentId}.");
        if (WaitForStage.Includes(Value(context, Stages), content.Stage))
        {
            context.Set(AssigneeId, content.AssigneeId);
            return WaitStep.Done(Reached);
        }

        var unit = UnitInput(context, UnitId) ?? content.UnitId ?? RememberedUnit(context)
            ?? throw new InvalidOperationException($"Content {contentId} has no unit whose shift could take it.");
        var wait = WaitOn(context, Subjects.Content(contentId), Subjects.Unit(unit));
        var running = await shifts.RunningAsync(unit, ct);

        if (!string.IsNullOrEmpty(content.AssigneeId))
        {
            var held = await HeldInAsync(context, content.AssigneeId, running, ct);
            // Someone who is not on a shift of the unit was given the work by hand: it stays theirs.
            if (held is null) return wait;

            // Handover: the shift the work is held in has ended.
            if (running.Any(s => s.Id == held) || !await shifts.HasEndedAsync(held.Value, ct)) return wait;

            // Someone who goes on into a running shift keeps the work.
            if (ShiftPicker.Candidates(running, role, null).FirstOrDefault(c => c.UserId == content.AssigneeId) is { } same)
            {
                Hold(context, same.UserId, same.ShiftId);
                return wait;
            }
        }
        else if (Now(context) < assignAfter)
        {
            return wait.Until(assignAfter);
        }

        if (await ShiftPicker.LeastLoadedAsync(context, running, role, null, ct) is not { } picked)
            return wait.Until(Soon(context));

        var answer = await api.AssignAsync(content, picked.UserId, ct);
        if (answer.IsSuccess)
        {
            Hold(context, picked.UserId, picked.ShiftId);
            context.Set(AssigneeId, picked.UserId);
        }
        else
        {
            Log(context).LogRefusedAssignment(contentId, (int)answer.Status, answer.Reason());
        }

        return wait.Until(Soon(context));
    }

    /// <summary>
    /// The shift in which the assignee holds the work: the one remembered for them, else the running shift whose rota
    /// they are on. Null when they were never seen on one.
    /// </summary>
    private static Task<int?> HeldInAsync(
        ActivityExecutionContext context, string assignee, IReadOnlyList<ShiftSnapshot> running, CancellationToken ct)
    {
        if (context.GetProperty<string>(HolderProperty) == assignee &&
            int.TryParse(context.GetProperty<string>(ShiftProperty), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var remembered))
            return Task.FromResult<int?>(remembered);

        var on = running.OrderBy(s => s.Id).FirstOrDefault(s => s.Rota.Any(m => m.UserId == assignee));
        if (on is null) return Task.FromResult<int?>(null);

        Hold(context, assignee, on.Id);
        return Task.FromResult<int?>(on.Id);
    }

    private static void Hold(ActivityExecutionContext context, string userId, int shiftId)
    {
        context.SetProperty(HolderProperty, userId);
        context.SetProperty(ShiftProperty, shiftId.ToString(CultureInfo.InvariantCulture));
    }
}

[Activity("SoftSquare.Newsroom", "Newsroom",
    "Runs one sub-workflow for each content of the post, and waits until the post is produced: its required contents are accepted, or the post was submitted.",
    DisplayName = "Produce contents")]
[FlowNode(Produced, Refiled, ProjectEnded)]
public sealed class ProduceContents : NewsroomActivity
{
    public const string Produced = "Produced";
    public const string Refiled = "Refiled";
    private const string Accepted = "accepted";

    public Input<string?> ProjectId { get; set; } = default!;
    public Input<string?> PostId { get; set; } = default!;

    /// <summary>The definition each content runs, instead of the one bound to its kind.</summary>
    public Input<string?> ContentDefinitionId { get; set; } = default!;

    protected override async Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct)
    {
        var projectId = RequiredProject(context, ProjectId);
        var postId = RequiredPost(context, PostId);
        var wait = WaitStep.For(Subjects.Project(projectId), Subjects.Post(postId));

        var project = await ProjectAsync(context, projectId, ct);
        if (EndOf(project) is { } end) return end;
        if (ProjectStates.Is(project.State, ProjectStates.NeedsModeration)) return wait;

        var post = await context.GetRequiredService<ContentModuleApi>().PostAsync(postId, ct)
            ?? throw new PlatformUnavailableException($"Content does not serve post {postId}.");
        if (Filing.Changed(Remembered(context, Filing.Property), ApprovalOf(context, project), project, post))
            return WaitStep.Done(Refiled);

        var contents = (post.Contents ?? []).Where(c => !c.Archived).ToArray();
        var section = project.SectionId ?? RememberedUnit(context)
            ?? throw new InvalidOperationException($"Project {projectId} has no Section.");
        var children = context.GetRequiredService<IContentWorkflows>();
        foreach (var content in contents)
            await children.EnsureStartedAsync(new ContentWork(
                projectId, postId, section, content.ContentId, content.FieldKey, post.Categories,
                Value(context, ContentDefinitionId), context.WorkflowExecutionContext.Id), ct);

        wait = wait with { Watch = [.. wait.Watch!, .. contents.Select(c => Subjects.Content(c.ContentId))] };

        // A returned post is produced again once it is submitted again.
        if (post.State == "Returned") return wait;
        if (post.State != "Draft") return WaitStep.Done(Produced);

        var required = contents.Where(c => c.Required).ToArray();
        return required.Length > 0 &&
               required.All(c => string.Equals(c.Stage, Accepted, StringComparison.OrdinalIgnoreCase))
            ? WaitStep.Done(Produced)
            : wait;
    }
}
