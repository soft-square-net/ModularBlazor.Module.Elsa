using System.Text.Json;
using Elsa.Common.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.DryRun;

/// <summary>A binding tried against a post.</summary>
/// <param name="UnitId">The Section; the post's unit, or its project's Section, when not set.</param>
/// <param name="DefinitionId">The definition to try; the one bound to the post's kind in the unit when not set.</param>
public sealed record DryRunRequest(int PostId, Guid? UnitId = null, string? DefinitionId = null, int? DefinitionVersion = null);

/// <summary>One step the workflow would take.</summary>
/// <param name="Outcome">The outcome the run follows; null where the run cannot go on from here.</param>
/// <param name="WouldWait">Whether the step would wait if it were reached now.</param>
/// <param name="Steps">The steps of the sub-workflow a content would run.</param>
public sealed record DryRunStep(
    string ActivityId, string Activity, string Description, string? Outcome, bool WouldWait,
    DryRunWorkflow? Steps = null);

public sealed record DryRunWorkflow(string DefinitionId, int Version, Guid? ContentId, IReadOnlyList<DryRunStep> Steps);

/// <param name="Problems">What would keep the workflow from running, or running on.</param>
public sealed record DryRunResult(int PostId, Guid? UnitId, DryRunWorkflow? Workflow, IReadOnlyList<string> Problems);

/// <summary>
/// Says what a workflow would do with a post as things are now, and changes nothing: no instance is made and no module
/// is asked to do anything. It follows the way the workflow takes when every wait is met in the end, except where the
/// current state already decides otherwise.
/// </summary>
internal sealed class DryRunner(
    IWorkflowDefinitionService definitions,
    IWorkflowBindings bindings,
    IUnitRouter router,
    IShiftWorkload workload,
    ContentModuleApi content,
    ShiftsModuleApi shifts,
    OrganizationModuleApi organization,
    IConfiguration settings)
{
    private const int StepLimit = 100;

    private sealed class Run
    {
        public required PostSnapshot Post { get; init; }
        public required Guid? Unit { get; set; }
        public Guid? ContentId { get; init; }
        public string? ContentKind { get; init; }
        public int Depth { get; init; }
        public required List<string> Problems { get; init; }
        public List<DryRunStep> Steps { get; } = [];
        public HashSet<IActivity> Seen { get; } = [];
    }

    private sealed record Prediction(string Description, string? Outcome, bool WouldWait = false, DryRunWorkflow? Steps = null);

    public async Task<DryRunResult?> RunAsync(DryRunRequest request, CancellationToken ct)
    {
        if (await content.PostAsync(request.PostId, ct) is not { } post) return null;

        var problems = new List<string>();
        var unit = request.UnitId ?? post.UnitId;
        if (unit is null) problems.Add("The post is in no unit: give the Section as unitId.");

        WorkflowChoice? choice = null;
        if (request.DefinitionId is { Length: > 0 } given)
            choice = new WorkflowChoice(given, request.DefinitionVersion);
        else if (unit is { } section && post.PostKind is { Length: > 0 } kind)
        {
            choice = await bindings.ForPostKindAsync(kind, section, ct);
            if (choice is null) problems.Add($"No workflow is bound to the post kind {kind} in the unit or above it.");
        }

        var run = new Run { Post = post, Unit = unit, Problems = problems };
        var workflow = choice is null ? null : await WorkflowAsync(choice, run, ct);
        return new DryRunResult(post.Id, unit, workflow, problems);
    }

    private async Task<DryRunWorkflow?> WorkflowAsync(WorkflowChoice choice, Run run, CancellationToken ct)
    {
        var version = choice.Version is { } number ? VersionOptions.SpecificVersion(number) : VersionOptions.Published;
        var graph = await definitions.FindWorkflowGraphAsync(
            WorkflowDefinitionHandle.ByDefinitionId(choice.DefinitionId, version), ct);
        if (graph is null)
        {
            run.Problems.Add($"There is no workflow definition '{choice.DefinitionId}'" +
                             (choice.Version is { } v ? $" version {v}." : " that is published."));
            return null;
        }

        await WalkAsync(graph.Workflow.Root, run, ct);
        return new DryRunWorkflow(choice.DefinitionId, graph.Workflow.Identity.Version, run.ContentId, run.Steps);
    }

    /// <summary>Walks the activity. False when the run cannot go on after it.</summary>
    private async Task<bool> WalkAsync(IActivity activity, Run run, CancellationToken ct)
    {
        switch (activity)
        {
            case Sequence sequence:
                foreach (var child in sequence.Activities)
                    if (!await WalkAsync(child, run, ct))
                        return false;
                return true;

            case Flowchart flowchart:
                var targets = flowchart.Connections.Select(c => c.Target.Activity).ToHashSet();
                var start = flowchart.Start ?? flowchart.Activities.FirstOrDefault(a => !targets.Contains(a));
                var next = new Queue<IActivity>();
                if (start is not null) next.Enqueue(start);
                var goesOn = true;
                while (next.TryDequeue(out var current))
                {
                    if (!run.Seen.Add(current) || run.Steps.Count >= StepLimit) continue;
                    var (outcome, canGoOn) = await StepAsync(current, run, ct);
                    goesOn &= canGoOn;
                    if (!canGoOn) continue;
                    foreach (var connection in flowchart.Connections.Where(c =>
                                 c.Source.Activity == current && (c.Source.Port ?? "Done") == outcome))
                        next.Enqueue(connection.Target.Activity);
                }

                return goesOn;

            default:
                return (await StepAsync(activity, run, ct)).CanGoOn;
        }
    }

    private async Task<(string Outcome, bool CanGoOn)> StepAsync(IActivity activity, Run run, CancellationToken ct)
    {
        if (activity is Sequence or Flowchart) return ("Done", await WalkAsync(activity, run, ct));

        var prediction = await PredictAsync(activity, run, ct);
        run.Steps.Add(new DryRunStep(activity.Id, activity.GetType().Name, prediction.Description, prediction.Outcome,
            prediction.WouldWait, prediction.Steps));
        return (prediction.Outcome ?? "", prediction.Outcome is not null);
    }

    private async Task<Prediction> PredictAsync(IActivity activity, Run run, CancellationToken ct)
    {
        switch (activity)
        {
            case RouteToUnit route:
            {
                var kind = Known(route.ContentKind);
                if (kind is null) return new("Routes a content kind that is only known when it runs.", RouteToUnit.Routed);
                var unit = await router.RouteAsync(new RouteQuery(kind, Known(route.Categories)?.ToArray() ?? []), ct);
                return unit is null
                    ? new($"No routing rule leads to a unit for {kind}.", RouteToUnit.NoRoute)
                    : new($"Routes {kind} to unit {unit:D}.", RouteToUnit.Routed);
            }

            case EnsurePostInUnitCategories ensure:
            {
                if ((Id(ensure.UnitId) ?? run.Unit) is not { } unit)
                    return new("Has no Section to file the post in.", null, true);
                var served = await organization.CategoriesAsync(unit, ct);
                return served is not null && UnitCategories.Covers(served, run.Post.Categories)
                    ? new($"Files post {run.Post.Id} in unit {unit:D} and reports the project InProduction.",
                        EnsurePostInUnitCategories.Filed)
                    : new($"The unit {unit:D} does not serve the post's categories: the project goes back to the moderator.",
                        EnsurePostInUnitCategories.NeedsModeration);
            }

            case WaitForRefiling:
                return new("Waits for the moderator, or for the post's categories or unit to change.", null, true);

            case FileContentInUnit file:
            {
                var contentId = Id(file.ContentId) ?? run.ContentId;
                var kind = Known(file.ContentKind) ?? run.ContentKind;
                var unit = Id(file.UnitId) ??
                           (kind is null ? null : await router.RouteAsync(new RouteQuery(kind, []), ct)) ?? run.Unit;
                run.Unit = unit;
                return new($"Gives content {contentId:D} to unit {unit:D}.", FileContentInUnit.Filed);
            }

            case WorkInShift work:
                return await AssignmentAsync(Id(work.ContentId) ?? run.ContentId, Id(work.UnitId) ?? run.Unit,
                    Known(work.RoleKey), Known(work.ManagerDelay), "keeps it assigned across shifts until it is " +
                    Stages(work.Stages), WorkInShift.Reached, run, ct);

            case AssignInShift assign:
                return await AssignmentAsync(Id(assign.ContentId) ?? run.ContentId, Id(assign.UnitId) ?? run.Unit,
                    Known(assign.RoleKey), Known(assign.ManagerDelay), "", AssignInShift.Assigned, run, ct);

            case WaitForStage wait:
            {
                var stages = Stages(wait.Stages);
                var met = await StageMetAsync(Id(wait.ContentId) ?? run.ContentId, wait.Stages, ct);
                return new($"Waits until the content is {stages}.", WaitForStage.Reached, !met);
            }

            case Deadline deadline:
            {
                var due = Known(deadline.Due) is { } at ? $"by {at:u}"
                    : Known(deadline.DueIn) is { } within ? $"within {within}" : "by a time known when it runs";
                var met = await StageMetAsync(Id(deadline.ContentId) ?? run.ContentId, deadline.Stages, ct);
                return new($"Expects the content to be {Stages(deadline.Stages)} {due}, and tells the shift's lead or the " +
                           "unit's manager when it is not.", Deadline.Met, !met);
            }

            case ProduceContents produce:
                return await ContentsAsync(produce, run, ct);

            case DeskReview desk:
            {
                var role = Known(desk.ReviewerRoleKey);
                var reviewer = role is { Length: > 0 }
                    ? $"after {Known(desk.ReviewerDelay) ?? TimeSpan.Zero} assigns a reviewer of the role {role}"
                    : "assigns no reviewer (no reviewer role is set)";
                return new($"Reports the project ReadyForDesk, {reviewer}, and waits for the desk.",
                    run.Post.State == "Returned" ? DeskReview.Returned : DeskReview.Approved,
                    run.Post.State is not ("Approved" or "Scheduled" or "Published" or "Returned") ||
                    run.Post is { State: "Approved", ScheduleTakenBack: true });
            }

            case WaitForPublication:
                return new("Waits for the desk to publish or schedule the post in Content; the workflow publishes nothing.",
                    WaitForPublication.Published, run.Post.State != "Published");

            case PublishPost publish:
                return new(Known(publish.PublishAt) is { } when
                        ? $"Schedules the post for {when:u} and publishes it then."
                        : "Publishes the post.", PublishPost.Published, false);

            case Notify notify:
            {
                var to = Known(notify.UserId) is { Length: > 0 } user ? $"user {user}"
                    : Known(notify.ToProjectAuthor) == true ? "the person who proposed the project"
                    : Known(notify.UnitPath) is { Length: > 0 } path ? $"unit {path}" : "someone known when it runs";
                return new($"Notifies {to}.", Notify.Sent);
            }

            default:
                return new("Not a newsroom activity: not tried, and taken to end as usual.", "Done");
        }
    }

    private async Task<Prediction> AssignmentAsync(
        Guid? contentId, Guid? unit, string? role, TimeSpan? delay, string then, string outcome, Run run,
        CancellationToken ct)
    {
        var what = contentId is { } id ? $"content {id:D}" : "a content known when it runs";
        if (unit is null || role is not { Length: > 0 })
            return new($"Assigns {what}; the unit or the role is only known when it runs.", outcome);

        var current = contentId is { } found ? await content.ContentAsync(found, ct) : null;
        string who;
        var waits = false;
        if (current?.AssigneeId is { Length: > 0 } assignee)
        {
            who = $"it is with {assignee} already";
        }
        else
        {
            var candidates = ShiftPicker.Candidates(await shifts.RunningAsync(unit.Value, ct), role, null);
            if (candidates.Count == 0)
            {
                who = $"nobody with the role {role} is checked in to a running shift of unit {unit:D}, so it waits";
                waits = true;
            }
            else
            {
                var loads = await workload.OpenItemsAsync(candidates.Select(c => c.UserId).ToArray(), ct);
                var picked = candidates.OrderBy(c => loads.GetValueOrDefault(c.UserId))
                    .ThenBy(c => c.UserId, StringComparer.Ordinal).First();
                who = $"now it would go to {picked.UserId} (shift {picked.ShiftId})";
            }
        }

        var manager = delay is { } time && time > TimeSpan.Zero ? $"after the manager's {time}, " : "";
        var and = then.Length > 0 ? $"; {then}" : "";
        return new($"Assigns {what} to a {role} on shift: {manager}{who}{and}.", outcome, waits);
    }

    private async Task<Prediction> ContentsAsync(ProduceContents produce, Run run, CancellationToken ct)
    {
        var contents = (run.Post.Contents ?? []).Where(c => !c.Archived).ToArray();
        if (contents.Length == 0)
            return new("The post has no contents: waits until the post is submitted.", ProduceContents.Produced,
                run.Post.State is "Draft" or "Returned");

        // One step for the activity, and one more for each content it would run.
        foreach (var (item, index) in contents.Select((c, i) => (c, i)).SkipLast(1))
            run.Steps.Add(await ContentStepAsync(produce, item, index, run, ct));
        var last = await ContentStepAsync(produce, contents[^1], contents.Length - 1, run, ct);
        var waits = run.Post.State is "Draft" or "Returned" &&
                    contents.Any(c => c.Required && !string.Equals(c.Stage, "accepted", StringComparison.OrdinalIgnoreCase));
        return new(last.Description, ProduceContents.Produced, waits, last.Steps);
    }

    private async Task<DryRunStep> ContentStepAsync(
        ProduceContents produce, PostContentSnapshot item, int index, Run run, CancellationToken ct)
    {
        var unit = await router.RouteAsync(new RouteQuery(item.FieldKey, run.Post.Categories), ct) ?? run.Unit;
        var choice = Known(produce.ContentDefinitionId) is { Length: > 0 } given
            ? new WorkflowChoice(given)
            : (unit is { } at ? await bindings.ForContentAsync(item.FieldKey, at, ct) : null)
              ?? new WorkflowChoice(DefaultWorkflows.Content);

        DryRunWorkflow? sub = null;
        if (run.Depth < 2)
            sub = await WorkflowAsync(choice, new Run
            {
                Post = run.Post, Unit = unit, ContentId = item.ContentId, ContentKind = item.FieldKey,
                Depth = run.Depth + 1, Problems = run.Problems
            }, ct);

        return new DryRunStep($"{produce.Id}:{index + 1}", nameof(ProduceContents),
            $"Runs {choice.DefinitionId} for the {(item.Required ? "required" : "optional")} content {item.ContentId:D} " +
            $"({item.FieldKey}, now {item.Stage}).", ProduceContents.Produced, false, sub);
    }

    private async Task<bool> StageMetAsync(Guid? contentId, Input<ICollection<string>?>? stages, CancellationToken ct) =>
        contentId is { } id && await content.ContentAsync(id, ct) is { } current &&
        WaitForStage.Includes(Known(stages), current.Stage);

    private string Stages(Input<ICollection<string>?>? stages) =>
        Known(stages) is { Count: > 0 } wanted ? string.Join(" or ", wanted) : "accepted";

    private Guid? Id(Input<string?>? input) => Guid.TryParse(Known(input), out var id) ? id : null;

    /// <summary>
    /// An input's value as far as it is known without running anything: a literal, an object literal or a module
    /// setting. An expression is not evaluated, since a script could change something.
    /// </summary>
    private T? Known<T>(Input<T>? input)
    {
        if (input is null) return default;
        if (input is ISettingInput setting) return (T?)setting.Read(settings);
        if (input.Expression is not { Type: "Literal" or "Object", Value: { } value }) return default;
        if (value is T typed) return typed;

        try
        {
            var json = value as string ?? JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
            var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (type == typeof(string)) return (T)(object)json;
            if (type == typeof(TimeSpan)) return (T)(object)TimeSpan.Parse(json, System.Globalization.CultureInfo.InvariantCulture);
            return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or InvalidCastException)
        {
            return default;
        }
    }
}
