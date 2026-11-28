using System.Globalization;
using Elsa.Extensions;
using Elsa.Scheduling;
using Elsa.Scheduling.Bookmarks;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

/// <summary>What a newsroom activity does next: it ends with an outcome, or it waits.</summary>
/// <param name="Watch">The subjects whose signals make it look again; null keeps the ones it watches.</param>
/// <param name="WakeAt">When it must look again whether or not a signal comes.</param>
public sealed record WaitStep(string? Outcome, IReadOnlyCollection<string>? Watch, DateTimeOffset? WakeAt)
{
    public static WaitStep Done(string outcome) => new(outcome, null, null);

    public static WaitStep For(params string[] subjects) => new(null, subjects, null);

    public WaitStep Until(DateTimeOffset wakeAt) => this with { WakeAt = wakeAt };
}

/// <summary>
/// An activity that looks at the other modules' current state and ends or waits. It never relies on what an event said:
/// a signal or a timer only makes it look again. So an event that comes before the activity waits, comes twice, or
/// never comes changes when the workflow goes on, never what it does.
/// <para>
/// While it waits it holds one signal bookmark per subject, which stays for as long as the subject is watched (a signal
/// that arrives while it is looking is not lost), and one timer. The only moment a signal can find nothing is before
/// the first bookmarks are saved; an activity that has just added one therefore looks once more shortly after.
/// Otherwise it looks again at <c>Elsa:Newsroom:RecheckInterval</c> at the latest.
/// </para>
/// <para>
/// A module that cannot be reached, or refuses the service, never faults the workflow: the activity waits and tries
/// again. It can run again after a crash, so whatever it does must be safe to repeat.
/// </para>
/// </summary>
public abstract class NewsroomActivity : Activity
{
    public const string RecheckSetting = "Elsa:Newsroom:RecheckInterval";
    public const string SettleSetting = "Elsa:Newsroom:SettleDelay";
    public const string RetrySetting = "Elsa:Newsroom:RetryDelay";

    /// <summary>The outcome of a wait whose project was published or killed meanwhile: there is nothing left to wait for.</summary>
    public const string ProjectEnded = "ProjectEnded";

    private const string ApprovalProperty = "approval";

    // Elsa 3.9.0's name for the input that marks an activity reached through a loop's backward connection.
    private const string BackwardInput = "BackwardConnection";
    private const string BackwardProperty = "newsroom.reachedBackward";

    protected abstract Task<WaitStep> StepAsync(ActivityExecutionContext context, CancellationToken ct);

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        if (context.ActivityInput.TryGetValue(BackwardInput, out var backward) && backward is true)
            context.SetProperty(BackwardProperty, true);
        return AdvanceAsync(context);
    }

    private ValueTask OnSignalAsync(ActivityExecutionContext context) => AdvanceAsync(context);

    private ValueTask OnTimerAsync(ActivityExecutionContext context) => AdvanceAsync(context);

    private async ValueTask AdvanceAsync(ActivityExecutionContext context)
    {
        var clock = context.GetRequiredService<TimeProvider>();
        var settings = context.GetRequiredService<IConfiguration>();

        WaitStep step;
        try
        {
            step = await StepAsync(context, context.CancellationToken);
        }
        catch (PlatformUnavailableException exception)
        {
            context.GetRequiredService<ILoggerFactory>().CreateLogger(GetType())
                .LogWarning(exception, "Workflow instance {InstanceId} waits to try again.", context.WorkflowExecutionContext.Id);
            step = new WaitStep(null, null, Soon(context));
        }

        if (step.Outcome is { } outcome)
        {
            context.ClearBookmarks();
            // The flowchart counts visits by that mark, but Elsa forgets an activity's input once it has waited. Without
            // it the counts drift, and a later loop silently ends the workflow instead of reaching its next activity.
            if (context.GetProperty<bool>(BackwardProperty)) context.ActivityInput[BackwardInput] = true;
            await context.CompleteActivityWithOutcomesAsync(outcome);
            return;
        }

        var now = clock.GetUtcNow();
        var wake = now + settings.GetValue(RecheckSetting, TimeSpan.FromMinutes(5));
        if (step.WakeAt is { } asked && asked < wake) wake = asked;

        if (step.Watch is { } subjects && Watch(context, subjects))
        {
            var settle = now + settings.GetValue(SettleSetting, TimeSpan.FromSeconds(2));
            if (settle < wake) wake = settle;
        }

        var earliest = now + TimeSpan.FromMilliseconds(500);
        SetTimer(context, wake < earliest ? earliest : wake);
    }

    /// <summary>Makes the signal bookmarks match the subjects. True when one was added.</summary>
    private bool Watch(ActivityExecutionContext context, IReadOnlyCollection<string> subjects)
    {
        var hasher = context.GetRequiredService<IStimulusHasher>();
        var wanted = subjects.Distinct().ToDictionary(s => hasher.Hash(NewsroomSignal.BookmarkName, new NewsroomSignal(s)));
        var held = context.Bookmarks.Where(b => b.Name == NewsroomSignal.BookmarkName).ToArray();

        foreach (var stale in held.Where(b => !wanted.ContainsKey(b.Hash)))
            context.WorkflowExecutionContext.Bookmarks.Remove(stale);

        var added = false;
        foreach (var (hash, subject) in wanted)
        {
            if (held.Any(b => b.Hash == hash)) continue;
            context.CreateBookmark(new CreateBookmarkArgs
            {
                BookmarkName = NewsroomSignal.BookmarkName,
                Stimulus = new NewsroomSignal(subject),
                Callback = OnSignalAsync,
                IncludeActivityInstanceId = false, // so that a signal finds it by its subject alone
                AutoBurn = false,
                AutoComplete = false
            });
            added = true;
        }

        return added;
    }

    private void SetTimer(ActivityExecutionContext context, DateTimeOffset wake)
    {
        foreach (var timer in context.Bookmarks.Where(b => b.Name == SchedulingStimulusNames.Delay).ToArray())
            context.WorkflowExecutionContext.Bookmarks.Remove(timer);

        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = SchedulingStimulusNames.Delay,
            Stimulus = new DelayPayload(wake),
            Callback = OnTimerAsync,
            AutoComplete = false
        });
    }

    /// <summary>When to try again after something that should pass: a conflict, or a module that did not answer.</summary>
    protected static DateTimeOffset Soon(ActivityExecutionContext context) =>
        context.GetRequiredService<TimeProvider>().GetUtcNow() +
        context.GetRequiredService<IConfiguration>().GetValue(RetrySetting, TimeSpan.FromSeconds(30));

    protected static DateTimeOffset Now(ActivityExecutionContext context) =>
        context.GetRequiredService<TimeProvider>().GetUtcNow();

    protected static ILogger Log(ActivityExecutionContext context) =>
        context.GetRequiredService<ILoggerFactory>().CreateLogger(context.Activity.GetType());

    /// <summary>An input's value, or null when the activity was given none.</summary>
    protected static T? Value<T>(ActivityExecutionContext context, Input<T>? input) =>
        input is null ? default : context.Get(input);

    /// <summary>The project the workflow runs for; null in a workflow that was not started for one.</summary>
    protected static Guid? RunningProject(ActivityExecutionContext context) =>
        Guid.TryParse(Remembered(context, ProjectInput.ProjectId), out var id) ? id : null;

    /// <summary>
    /// A wait on the subjects. In a project's workflow it also hears about the project, so that it can end when the
    /// project does (<see cref="ProjectEndAsync"/>).
    /// </summary>
    protected static WaitStep WaitOn(ActivityExecutionContext context, params string[] subjects) =>
        WaitStep.For(RunningProject(context) is { } project ? [.. subjects, Subjects.Project(project)] : subjects);

    /// <summary>
    /// How a wait ends with the workflow's project; null while the project goes on. See <see cref="EndOf"/>.
    /// </summary>
    protected static async Task<WaitStep?> ProjectEndAsync(ActivityExecutionContext context, CancellationToken ct) =>
        RunningProject(context) is { } id ? EndOf(await ProjectAsync(context, id, ct)) : null;

    /// <summary>
    /// A project that was published, in the workflow or outside it (NewsRoom follows Content), leaves nothing to wait
    /// for: the activity ends with <see cref="ProjectEnded"/>. A killed one is for the kill's event to cancel: until it
    /// comes the activity does nothing, so that a killed project's workflows always end cancelled, whichever of them
    /// looks first.
    /// </summary>
    protected static WaitStep? EndOf(ProjectSnapshot project)
    {
        if (ProjectStates.Is(project.State, ProjectStates.Published)) return WaitStep.Done(ProjectEnded);
        return ProjectStates.Is(project.State, ProjectStates.Killed) ? WaitStep.For(Subjects.Project(project.Id)) : null;
    }

    protected static bool IsEnded(ProjectSnapshot project) => EndOf(project) is not null;

    protected static Guid RequiredContent(ActivityExecutionContext context, Input<string?>? input) =>
        Guid.TryParse(Text(Value(context, input)) ?? Remembered(context, ProjectInput.ContentId), out var id)
            ? id
            : throw new InvalidOperationException("The activity needs a ContentId, or a workflow started for a content.");

    /// <summary>
    /// A moment counted from the first look, and kept, so that a restart does not start the delay again.
    /// </summary>
    protected static DateTimeOffset KeptTime(ActivityExecutionContext context, string property, Func<DateTimeOffset> first)
    {
        if (context.GetProperty<string>(property) is not { } stored)
        {
            stored = first().ToString("O", CultureInfo.InvariantCulture);
            context.SetProperty(property, stored);
        }

        return DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture);
    }

    /// <summary>A value the workflow keeps, or the one it was started with.</summary>
    protected static string? Remembered(ActivityExecutionContext context, string name)
    {
        var workflow = context.WorkflowExecutionContext;
        var found = workflow.Properties.TryGetValue(ProjectInput.Property(name), out var value) ||
                    workflow.Input.TryGetValue(name, out value);
        return found ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    protected static void Remember(ActivityExecutionContext context, string name, string value) =>
        context.WorkflowExecutionContext.Properties[ProjectInput.Property(name)] = value;

    protected static Guid RequiredProject(ActivityExecutionContext context, Input<string?>? input) =>
        Guid.TryParse(Text(Value(context, input)) ?? Remembered(context, ProjectInput.ProjectId), out var id)
            ? id
            : throw new InvalidOperationException("The activity has no project: set ProjectId, or start the workflow for a project.");

    protected static int RequiredPost(ActivityExecutionContext context, Input<string?>? input) =>
        int.TryParse(Text(Value(context, input)) ?? Remembered(context, ProjectInput.PostId), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new InvalidOperationException("The activity has no post: set PostId, or start the workflow for a project.");

    protected static Guid RequiredId(ActivityExecutionContext context, Input<string?>? input, string name) =>
        Guid.TryParse(Value(context, input), out var id)
            ? id
            : throw new InvalidOperationException($"The activity needs a {name}.");

    protected static Guid? UnitInput(ActivityExecutionContext context, Input<string?>? input) =>
        Guid.TryParse(Value(context, input), out var id) ? id : null;

    protected static Guid? RememberedUnit(ActivityExecutionContext context) =>
        Guid.TryParse(Remembered(context, ProjectInput.UnitId), out var id) ? id : null;

    /// <summary>
    /// The approval the workflow is working on: the one whose filing it checked. NewsRoom refuses a report about an
    /// older one, so a late retry cannot skip the check of a newer approval.
    /// </summary>
    protected static int ApprovalOf(ActivityExecutionContext context, ProjectSnapshot project) =>
        int.TryParse(Remembered(context, ApprovalProperty), out var approval) ? approval : project.ApprovalNumber;

    protected static void RememberApproval(ActivityExecutionContext context, int approval) =>
        Remember(context, ApprovalProperty, approval.ToString(CultureInfo.InvariantCulture));

    protected static async Task<ProjectSnapshot> ProjectAsync(ActivityExecutionContext context, Guid id, CancellationToken ct) =>
        await context.GetRequiredService<INewsRoomProjects>().GetAsync(id, ct)
        ?? throw new PlatformUnavailableException($"NewsRoom does not serve project {id}.");

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
