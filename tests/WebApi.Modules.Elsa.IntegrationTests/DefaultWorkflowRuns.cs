using Elsa.Common.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.DryRun;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;

namespace soft.square.net.WebApi.Modules.Elsa.IntegrationTests;

/// <summary>The default workflows, their sub-workflows, the shift handover and the dry run.</summary>
[Collection(EngineTests.Collection)]
public class DefaultWorkflowRuns(PostgresServer server) : IAsyncLifetime
{
    private const string Writer = "wanda";
    private const string Editor = "edith";

    private readonly FakePlatform _platform = new();
    private readonly RecordingMessenger _messenger = new();
    private readonly List<Replica> _replicas = [];
    private readonly Guid _section = Guid.NewGuid();
    private readonly Guid _desk = Guid.NewGuid();
    private readonly Guid _contentId = Guid.NewGuid();
    private FakeProject _project = null!;
    private string _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeContent Content => _platform.Contents[_contentId];
    private FakePost Post => _platform.Posts[42];
    private string MainId => NewsroomWorkflows.InstanceId(Tenants.A, _project.Id);
    private string ChildId => NewsroomWorkflows.InstanceId(Tenants.A, _project.Id, _contentId);

    public async ValueTask InitializeAsync()
    {
        _database = await server.NewDatabaseAsync(Ct);
        _project = new FakeProject { Id = Guid.NewGuid(), SectionId = _section, PostId = 42 };
        _platform.Projects[_project.Id] = _project;
        _platform.Posts[42] = new FakePost { Id = 42 };
        _platform.Contents[_contentId] = new FakeContent { Id = _contentId, UnitId = _section, FieldKey = "video" };
        Post.Attached.Add((_contentId, true));
        _platform.UnitCategories[_section] = ["news"];
        _platform.Routes["video"] = _desk;
        _platform.PostBinding = DefaultWorkflows.Post;
        _platform.PostBindingVersion = 1; // the tests of version 2 say so
        _platform.ShiftUnit = _desk;
        _platform.Rota.Add(new RotaMember(Writer, "writer", false, DateTimeOffset.UtcNow, null));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var replica in _replicas) await replica.DisposeAsync();
    }

    private async Task<Replica> ReplicaAsync(Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?> { ["Elsa:Newsroom:ManagerDelay"] = "00:00:00" };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        var replica = await Replica.StartAsync(_database, _platform, _messenger, all);
        _replicas.Add(replica);
        return replica;
    }

    private static async Task<WorkflowInstance?> InstanceAsync(Replica replica, string id) =>
        (await replica.InstancesAsync(Tenants.A)).SingleOrDefault(i => i.Id == id);

    private static async Task EndsAsync(Replica replica, string id, WorkflowSubStatus status, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var instance = await InstanceAsync(replica, id);
            if (instance?.SubStatus == status)
            {
                instance.WorkflowState.Incidents.ShouldBeEmpty();
                return;
            }

            if (DateTime.UtcNow > until)
                throw new TimeoutException($"{id} did not become {status}: it is {instance?.SubStatus.ToString() ?? "missing"} " +
                    string.Join("|", instance?.WorkflowState.Incidents.Select(x => x.Message) ?? []));
            await Task.Delay(200, Ct);
        }
    }

    private Task PostChangedAsync(Replica replica) =>
        replica.DeliverAsync(Tenants.A, "content.post-state-changed.v1", "post:42", new { });

    private Task ContentChangedAsync(Replica replica) => replica.DeliverAsync(
        Tenants.A, "content.work-item-state-changed.v1", $"content:content:{_contentId:D}", new { });

    private Task ShiftEndedAsync(Replica replica, Guid unit) =>
        replica.DeliverAsync(Tenants.A, "shifts.ended.v1", "shift:1", new { shift = new { unitId = unit } });

    private void SetPost(string state)
    {
        _platform.Change(() =>
        {
            Post.State = state;
            Post.Version++;
            Post.History.Add(state.ToLowerInvariant()); // as Content records an approval, a return, a submission
        });
    }

    private async Task AcceptContentAsync(Replica replica)
    {
        _platform.Change(() => Content.Stage = "accepted");
        await ContentChangedAsync(replica);
    }

    /// <summary>Until the desk waits for the post: filed, produced and sent on.</summary>
    private async Task ToTheDeskAsync(Replica replica)
    {
        await replica.ApproveAsync(Tenants.A, _project);
        await Replica.UntilAsync(() => Content.AssigneeId == Writer, "the content assigned");
        await AcceptContentAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk");
    }

    [Fact]
    public async Task TheDefaultWorkflowsTakeAPostFromApprovalToPublication()
    {
        var replica = await ReplicaAsync();

        await replica.ApproveAsync(Tenants.A, _project);

        // The content goes to the unit that produces its kind, and to the writer on shift there.
        await Replica.UntilAsync(() => Content.AssigneeId == Writer, "the content assigned");
        Content.UnitId.ShouldBe(_desk);
        _project.State.ShouldBe(ProjectStates.InProduction);
        var instances = await replica.InstancesAsync(Tenants.A);
        instances.Select(i => i.Id).Order().ShouldBe(new[] { MainId, ChildId }.Order());
        instances.ShouldAllBe(i => i.CorrelationId == ProjectInput.CorrelationId(_project.Id));
        instances.Single(i => i.Id == ChildId).ParentWorkflowInstanceId.ShouldBe(MainId);
        instances.Single(i => i.Id == ChildId).DefinitionId.ShouldBe(DefaultWorkflows.Content);

        await AcceptContentAsync(replica);
        await EndsAsync(replica, ChildId, WorkflowSubStatus.Finished);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk");

        SetPost("Approved");
        await PostChangedAsync(replica);

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _project.State.ShouldBe(ProjectStates.Published);
        Post.State.ShouldBe("Published");
        var announced = _messenger.Sent.Single();
        (announced.Target, announced.Type).ShouldBe(("author", "workflow.published"));
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
        _platform.Count("/accept").ShouldBe(0); // no reviewer role is set: people assign reviews
    }

    [Fact]
    public async Task AReturnedPostIsProducedAndReviewedAgain()
    {
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);

        SetPost("Returned");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.InProduction, "the project back in production");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        _project.State.ShouldBe(ProjectStates.InProduction); // not ready while the post is still returned

        SetPost("Submitted");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk again");

        // Returned a second time: the same loop.
        SetPost("Returned");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.InProduction, "the project back in production");
        SetPost("Submitted");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk again");

        SetPost("Approved");
        await PostChangedAsync(replica);
        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(1);
    }

    [Fact]
    public async Task ARefusedPublicationGoesBackToTheDeskUntilThePostChanges()
    {
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);
        _platform.PublishRefusal = "The post's fields are not valid.";

        SetPost("Approved");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _messenger.Sent.Any(m => m.Type == "workflow.publish-refused"), "the refusal told");
        await PostChangedAsync(replica);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(1);
        (await InstanceAsync(replica, MainId))!.SubStatus.ShouldBe(WorkflowSubStatus.Suspended);

        _platform.PublishRefusal = null;
        SetPost("Approved"); // corrected and approved again: a newer version
        await PostChangedAsync(replica);

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(2);
        _messenger.Sent.Select(m => m.Type).ShouldBe(["workflow.publish-refused", "workflow.published"]);
    }

    [Fact]
    public async Task AKilledProjectCancelsItsSubWorkflowsToo()
    {
        var replica = await ReplicaAsync();
        await replica.ApproveAsync(Tenants.A, _project);
        await Replica.UntilAsync(() => Content.AssigneeId == Writer, "the content assigned");

        _platform.Change(() => _project.State = ProjectStates.Killed);
        // A sub-workflow that looks before the kill's event comes leaves the end to it.
        await ContentChangedAsync(replica);
        (await InstanceAsync(replica, ChildId))!.SubStatus.ShouldBe(WorkflowSubStatus.Suspended);
        await replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}",
            new { projectId = _project.Id, state = "Killed" });

        await EndsAsync(replica, MainId, WorkflowSubStatus.Cancelled, seconds: 5);
        await EndsAsync(replica, ChildId, WorkflowSubStatus.Cancelled, seconds: 5);
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
    }

    [Fact]
    public async Task APostPublishedOutsideTheWorkflowEndsEveryWait()
    {
        // A long manager's delay: the assignment is still pending when the post is published by hand.
        var replica = await ReplicaAsync(new() { ["Elsa:Newsroom:ManagerDelay"] = "01:00:00" });
        await replica.ApproveAsync(Tenants.A, _project);
        await Replica.UntilAsync(() => _project.State == ProjectStates.InProduction, "the project in production");
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);

        // NewsRoom follows Content, and says so.
        _platform.Change(() => { Post.State = "Published"; Post.Version++; _project.State = ProjectStates.Published; });
        await replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}",
            new { projectId = _project.Id, state = "Published" });

        await EndsAsync(replica, ChildId, WorkflowSubStatus.Finished);
        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
        Content.AssigneeId.ShouldBeNull();
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(0);
    }

    [Fact]
    public async Task AStageWaitAndADeadlineEndWithTheirProject()
    {
        var replica = await ReplicaAsync();
        _platform.PostBinding = "project-workflow";
        var content = new Input<string?>(_contentId.ToString());
        await replica.PublishAsync(Tenants.A, new Flowchart
        {
            Id = "root",
            Activities =
            {
                new Deadline { Id = "deadline", ContentId = content, DueIn = new Input<TimeSpan?>(TimeSpan.FromHours(1)) }
            }
        });
        await replica.ApproveAsync(Tenants.A, _project);

        _platform.Change(() => _project.State = ProjectStates.Published);
        await replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}",
            new { projectId = _project.Id, state = "Published" });

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished, seconds: 10);
        _messenger.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task WorkWhoseShiftEndedGoesToTheNextShiftOfTheUnit()
    {
        var replica = await ReplicaAsync();
        await replica.ApproveAsync(Tenants.A, _project);
        await Replica.UntilAsync(() => Content.AssigneeId == Writer, "the content assigned");

        // The next shift runs before the first ends: the work stays until it does.
        _platform.Change(() => _platform.Shifts.Add(new FakeShift
        {
            Id = 2, UnitId = _desk, Rota = [new RotaMember("nina", "writer", false, DateTimeOffset.UtcNow, null)]
        }));
        await ShiftEndedAsync(replica, _desk);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Content.AssigneeId.ShouldBe(Writer);

        _platform.Change(() => _platform.FirstShiftEnded = true);
        await ShiftEndedAsync(replica, _desk);

        await Replica.UntilAsync(() => Content.AssigneeId == "nina", "the content handed over");
    }

    [Fact]
    public async Task AReviewerIsAssignedAndAgainWhenContentReturnsTheReview()
    {
        var replica = await ReplicaAsync(new()
        {
            ["Elsa:Newsroom:ReviewerRole"] = "editor", ["Elsa:Newsroom:ReviewerDelay"] = "00:00:01"
        });
        _platform.Rota.Add(new RotaMember(Editor, "editor", true, DateTimeOffset.UtcNow, null));
        // The owner does not review their own post.
        _platform.Rota.Add(new RotaMember("author", "editor", false, DateTimeOffset.UtcNow, null));
        _platform.Loads[Editor] = 5;
        _platform.Change(() => Post.UnitId = null);
        await ToTheDeskAsync(replica);
        _platform.ShiftUnit.ShouldBe(_desk);
        // The post is filed in the Section: its reviewers are on the Section's shift.
        _platform.Change(() => _platform.Shifts.Add(new FakeShift
        {
            Id = 7, UnitId = _section, Rota =
            [
                new RotaMember(Editor, "editor", true, DateTimeOffset.UtcNow, null),
                new RotaMember("author", "editor", false, DateTimeOffset.UtcNow, null)
            ]
        }));

        SetPost("Submitted");
        await PostChangedAsync(replica);

        await Replica.UntilAsync(() => Post.State == "InReview", "the review assigned");
        Post.ReviewerId.ShouldBe(Editor);

        _platform.Change(() =>
        {
            _platform.Shifts.Single(s => s.Id == 7).Ended = true;
            _platform.Shifts.Add(new FakeShift
            {
                Id = 8, UnitId = _section, Rota = [new RotaMember("erik", "editor", true, DateTimeOffset.UtcNow, null)]
            });
        });
        await ShiftEndedAsync(replica, _section);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        // The handover is Content's: the workflow leaves the review where it is.
        (Post.State, Post.ReviewerId).ShouldBe(("InReview", Editor));
        _platform.Count("transfer").ShouldBe(0);

        _platform.Change(() => { Post.State = "Accepted"; Post.ReviewerId = null; Post.Version++; });
        await PostChangedAsync(replica);

        await Replica.UntilAsync(() => Post.ReviewerId == "erik", "the review assigned to the next shift");
        _platform.Count("POST /api/content/posts/42/assign").ShouldBe(2);
    }

    [Fact]
    public async Task AScheduledPostIsPublishedWhenItIsDue()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, new Sequence
        {
            Activities = { new PublishPost { PublishAt = new Input<DateTimeOffset?>(DateTimeOffset.UtcNow.AddSeconds(6)) } }
        });
        _platform.PostBinding = "project-workflow";
        SetPost("Approved");
        _project.State = ProjectStates.ReadyForDesk;

        await replica.ApproveAsync(Tenants.A, _project);

        await Replica.UntilAsync(() => _project.State == ProjectStates.Scheduled, "the project scheduled");
        Post.State.ShouldBe("Scheduled");
        _platform.Count("publish-due").ShouldBe(0);

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished, seconds: 20);
        _platform.Count("publish-due").ShouldBe(0); // Content publishes it, unasked
        _project.State.ShouldBe(ProjectStates.Published);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // the post changes between the read and the call: the event is delivered again
    public async Task KillingAScheduledProjectUnschedulesItsPost(int conflicts)
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, new Sequence
        {
            Activities = { new PublishPost { PublishAt = new Input<DateTimeOffset?>(DateTimeOffset.UtcNow.AddHours(1)) } }
        });
        _platform.PostBinding = "project-workflow";
        SetPost("Approved");
        _project.State = ProjectStates.ReadyForDesk;
        await replica.ApproveAsync(Tenants.A, _project);
        await Replica.UntilAsync(() => _project.State == ProjectStates.Scheduled, "the project scheduled");

        _platform.Change(() => { _project.State = ProjectStates.Killed; _platform.UnscheduleConflicts = conflicts; });
        var killed = new { projectId = _project.Id, state = "Killed" };
        Task Kill() => replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}", killed);
        if (conflicts > 0)
        {
            await Should.ThrowAsync<PlatformUnavailableException>(Kill);
            Post.State.ShouldBe("Scheduled");
        }

        await Kill();
        await Kill();

        await EndsAsync(replica, MainId, WorkflowSubStatus.Cancelled, seconds: 5);
        (Post.State, Post.ScheduledFor).ShouldBe(("Approved", (DateTimeOffset?)null));
        _platform.Count("POST /api/content/posts/42/unschedule").ShouldBe(conflicts + 1);
    }

    [Fact]
    public async Task AKilledProjectWithoutAWorkflowIsUnscheduledToo()
    {
        var replica = await ReplicaAsync();
        _platform.PostBinding = null;
        SetPost("Scheduled");
        _platform.Change(() =>
        {
            Post.ScheduledFor = DateTimeOffset.UtcNow.AddHours(1); // scheduled by hand
            _project.State = ProjectStates.Killed;
        });

        await replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}",
            new { projectId = _project.Id, state = "Killed" });

        Post.State.ShouldBe("Approved");
    }

    // The desk's moves in Content. NewsRoom follows Content's events; the fake does not, so the tests move the project.
    private async Task DeskSchedulesAsync(Replica replica, TimeSpan from)
    {
        _platform.Change(() =>
        {
            Post.State = "Scheduled";
            Post.ScheduledFor = DateTimeOffset.UtcNow + from;
            Post.History.Add("scheduled");
            Post.Version++;
        });
        await PostChangedAsync(replica);
    }

    private async Task DeskUnschedulesAsync(Replica replica)
    {
        _platform.Change(() =>
        {
            Post.State = "Approved";
            Post.ScheduledFor = null;
            Post.History.Add("unscheduled");
            Post.Version++;
            _project.State = ProjectStates.ReadyForDesk; // as NewsRoom does
        });
        await PostChangedAsync(replica);
    }

    /// <summary>The post stays as the desk left it, and the workflow asks Content for no publication.</summary>
    private async Task StaysWithTheDeskAsync(Replica replica, string state)
    {
        await PostChangedAsync(replica);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        await PostChangedAsync(replica);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Post.State.ShouldBe(state);
        (await InstanceAsync(replica, MainId))!.SubStatus.ShouldBe(WorkflowSubStatus.Suspended);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(0);
        _platform.Count("POST /api/content/posts/42/schedule").ShouldBe(0);
    }

    /// <summary>Until the one activity that holds timers and signals is the one named.</summary>
    private async Task UntilWaitingAtAsync(Replica replica, string activity)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var state = (await InstanceAsync(replica, MainId))!.WorkflowState;
            var waiting = state.Bookmarks.Select(b => b.ActivityId).Distinct().ToArray();
            if (waiting is [var only] && only == activity) return;
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"The workflow waits at {string.Join(", ", waiting)}, not at {activity}.");
            await Task.Delay(100, Ct);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task APostTheDeskUnschedulesByHandWaitsForTheDesk(int version)
    {
        _platform.PostBindingVersion = version;
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);
        // The reviewer approved, and the desk scheduled it before the workflow looked.
        _platform.Change(() => { Post.History.Add("approved"); Post.Version++; });
        await DeskSchedulesAsync(replica, TimeSpan.FromHours(1));
        await Replica.UntilAsync(() => _project.State == ProjectStates.Scheduled, "the project scheduled");

        await DeskUnschedulesAsync(replica);

        // Version 1 goes back to its desk review; version 2 never left the wait for the desk's publication.
        var waitsAt = version == 1 ? "desk" : "publication";
        await UntilWaitingAtAsync(replica, waitsAt);
        await StaysWithTheDeskAsync(replica, "Approved");
        _messenger.Sent.ShouldBeEmpty();

        // Scheduled again and taken back again: still the desk's.
        await DeskSchedulesAsync(replica, TimeSpan.FromHours(2));
        await Replica.UntilAsync(() => _project.State == ProjectStates.Scheduled, "the project scheduled again");
        await DeskUnschedulesAsync(replica);
        await UntilWaitingAtAsync(replica, waitsAt);
        // A change that keeps it approved is no approval.
        _platform.Change(() => { Post.History.Add("categories changed"); Post.Version++; });
        await StaysWithTheDeskAsync(replica, "Approved");

        // Scheduled for soon: Content publishes it when it is due, and the workflow ends.
        await DeskSchedulesAsync(replica, TimeSpan.FromSeconds(4));

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _project.State.ShouldBe(ProjectStates.Published);
        _messenger.Sent.Select(m => m.Type).ShouldBe(["workflow.published"]);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(0);
        _platform.Count("POST /api/content/posts/42/schedule").ShouldBe(0);
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task APostScheduledAndTakenBackWhileNothingLookedIsNotPublished(int version)
    {
        _platform.PostBindingVersion = version;
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);

        _platform.Change(() =>
        {
            Post.State = "Approved";
            Post.History.AddRange(["approved", "scheduled", "unscheduled"]);
            Post.Version += 3;
        });

        await StaysWithTheDeskAsync(replica, "Approved");
        _messenger.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReviewersApprovalPublishesNothingInVersionTwo()
    {
        _platform.PostBindingVersion = null; // a binding without a version runs the latest
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);
        (await InstanceAsync(replica, MainId))!.WorkflowState.DefinitionVersion.ShouldBe(2);

        SetPost("Approved");
        await PostChangedAsync(replica);

        await UntilWaitingAtAsync(replica, "publication");
        await StaysWithTheDeskAsync(replica, "Approved");
        _project.State.ShouldBe(ProjectStates.ReadyForDesk);

        // The desk publishes now.
        SetPost("Published");
        await PostChangedAsync(replica);

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _project.State.ShouldBe(ProjectStates.Published);
        _messenger.Sent.Select(m => m.Type).ShouldBe(["workflow.published"]);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(0);
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // returned while scheduled
    public async Task AReturnFromTheDeskGoesBackToProductionInVersionTwo(bool scheduled)
    {
        _platform.PostBindingVersion = 2;
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);
        SetPost("Approved");
        await PostChangedAsync(replica);
        await UntilWaitingAtAsync(replica, "publication");
        if (scheduled)
        {
            await DeskSchedulesAsync(replica, TimeSpan.FromHours(1));
            await Replica.UntilAsync(() => _project.State == ProjectStates.Scheduled, "the project scheduled");
            _platform.Change(() => _project.State = ProjectStates.InProduction); // as NewsRoom does
        }

        SetPost("Returned");
        await PostChangedAsync(replica);

        await UntilWaitingAtAsync(replica, "produce");
        _project.State.ShouldBe(ProjectStates.InProduction);
        SetPost("Submitted");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk again");
        SetPost("Approved"); // approved afresh: version 2 still publishes nothing
        await PostChangedAsync(replica);
        await UntilWaitingAtAsync(replica, "publication");
        await StaysWithTheDeskAsync(replica, "Approved");
        _messenger.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFreshApprovalAfterAnUnscheduleIsPublishedByVersionOneAgain()
    {
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);
        _platform.Change(() => { Post.History.Add("approved"); Post.Version++; });
        await DeskSchedulesAsync(replica, TimeSpan.FromHours(1));
        await Replica.UntilAsync(() => _project.State == ProjectStates.Scheduled, "the project scheduled");
        await DeskUnschedulesAsync(replica);
        await UntilWaitingAtAsync(replica, "desk");

        SetPost("Returned");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.InProduction, "the project back in production");
        SetPost("Submitted");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk again");
        SetPost("Approved");
        await PostChangedAsync(replica);

        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(1);
        _messenger.Sent.Select(m => m.Type).ShouldBe(["workflow.published"]);
    }

    private async Task SetPostAndWaitAtAsync(Replica replica, string state, string activity)
    {
        SetPost(state);
        await PostChangedAsync(replica);
        await UntilWaitingAtAsync(replica, activity);
    }

    /// <summary>
    /// Loops through activities that wait. Elsa 3.9.0 forgets, once an activity has waited, that a backward connection
    /// led to it; its flowchart then ends the run instead of scheduling a later activity.
    /// </summary>
    [Fact]
    public async Task EveryLoopOfVersionOneCanBeTakenAgain()
    {
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);

        // A refused publication, then returns from the desk the workflow went back to.
        _platform.PublishRefusal = "The post's fields are not valid.";
        SetPost("Approved");
        await PostChangedAsync(replica);
        await Replica.UntilAsync(() => _messenger.Sent.Any(m => m.Type == "workflow.publish-refused"), "the refusal told");
        await UntilWaitingAtAsync(replica, "desk");
        _platform.PublishRefusal = null;

        for (var round = 0; round < 3; round++)
        {
            await SetPostAndWaitAtAsync(replica, "Returned", "produce");
            await SetPostAndWaitAtAsync(replica, "Submitted", "desk");
        }

        await RefileAndReturnToTheDeskAsync(replica);
        await SetPostAndWaitAtAsync(replica, "Returned", "produce");
        await SetPostAndWaitAtAsync(replica, "Submitted", "desk");

        SetPost("Approved");
        await PostChangedAsync(replica);
        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        Post.State.ShouldBe("Published");
    }

    [Fact]
    public async Task EveryLoopOfVersionTwoCanBeTakenAgain()
    {
        _platform.PostBindingVersion = 2;
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);

        for (var round = 0; round < 2; round++)
        {
            await SetPostAndWaitAtAsync(replica, "Approved", "publication");
            // Returned and submitted again before the workflow looked: the desk review waits after a backward step.
            _platform.Change(() => Post.History.Add("returned"));
            await SetPostAndWaitAtAsync(replica, "Submitted", "desk");
            await SetPostAndWaitAtAsync(replica, "Returned", "produce");
            await SetPostAndWaitAtAsync(replica, "Submitted", "desk");
        }

        await RefileAndReturnToTheDeskAsync(replica);
        await SetPostAndWaitAtAsync(replica, "Approved", "publication");
        await SetPostAndWaitAtAsync(replica, "Returned", "produce");
        await SetPostAndWaitAtAsync(replica, "Submitted", "desk");
        await SetPostAndWaitAtAsync(replica, "Approved", "publication");

        SetPost("Published");
        await PostChangedAsync(replica);
        await EndsAsync(replica, MainId, WorkflowSubStatus.Finished);
        _messenger.Sent.Select(m => m.Type).ShouldBe(["workflow.published"]);
    }

    // Twice: the moderator is asked about categories the Section does not serve, and the post is filed again.
    private async Task RefileAndReturnToTheDeskAsync(Replica replica)
    {
        for (var round = 0; round < 2; round++)
        {
            _platform.Change(() => { Post.Categories = ["sport"]; Post.Version++; });
            await PostChangedAsync(replica);
            await UntilWaitingAtAsync(replica, "refile");
            _project.State.ShouldBe(ProjectStates.NeedsModeration);

            _platform.Change(() =>
            {
                Post.Categories = ["news"];
                Post.Version++;
                _project.State = ProjectStates.Approved;
                _project.ApprovalNumber++;
            });
            await replica.ApproveAsync(Tenants.A, _project, "newsroom.project-reapproved.v1");
            await UntilWaitingAtAsync(replica, "desk");
        }
    }

    [Fact]
    public async Task AReturnThatArrivesWhilePublishingRaisesNoRefusal()
    {
        var replica = await ReplicaAsync();
        await ToTheDeskAsync(replica);
        _platform.DuringPublish = post =>
        {
            post.State = "Returned";
            post.History.Add("returned");
            post.Version++;
        };

        SetPost("Approved");
        await PostChangedAsync(replica);

        await UntilWaitingAtAsync(replica, "produce");
        _project.State.ShouldBe(ProjectStates.InProduction);
        _messenger.Sent.ShouldBeEmpty();
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(1);
    }

    [Fact]
    public async Task AContentKindsBindingNamesItsSubWorkflow()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, new Sequence
        {
            Activities = { new WaitForStage { Stages = new Input<ICollection<string>?>(new List<string> { "approved" }) } }
        }, "tenant-a-video");
        _platform.ContentBindings["video"] = "tenant-a-video";

        await replica.ApproveAsync(Tenants.A, _project);

        await Replica.UntilAsync(() => _project.State == ProjectStates.InProduction, "the project in production");
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        (await InstanceAsync(replica, ChildId))!.DefinitionId.ShouldBe("tenant-a-video");
        _platform.Change(() => Content.Stage = "approved");
        await ContentChangedAsync(replica);
        await EndsAsync(replica, ChildId, WorkflowSubStatus.Finished, seconds: 10);
    }

    [Theory]
    [InlineData("newsroom.project-binding-available.v1")]
    [InlineData("newsroom.project-reapproved.v1")]
    public async Task AProjectWithoutABindingStartsOnceThereIsOne(string later)
    {
        var replica = await ReplicaAsync();
        _platform.PostBinding = null;

        // A setup error, not a failed delivery: the event is handled.
        await replica.ApproveAsync(Tenants.A, _project);
        (await replica.InstancesAsync(Tenants.A)).ShouldBeEmpty();
        _project.State.ShouldBe(ProjectStates.Approved);

        _platform.PostBinding = DefaultWorkflows.Post;
        await replica.ApproveAsync(Tenants.A, _project, later);
        await replica.ApproveAsync(Tenants.A, _project, later);

        await Replica.UntilAsync(() => Content.AssigneeId == Writer, "the content assigned");
        (await replica.InstancesAsync(Tenants.A)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task EachTenantRunsTheSameDefaultDefinitions()
    {
        var replica = await ReplicaAsync();
        await replica.ApproveAsync(Tenants.A, _project);
        await replica.ApproveAsync(Tenants.B, _project);

        var a = await replica.InstancesAsync(Tenants.A);
        var b = await replica.InstancesAsync(Tenants.B);

        a.Select(i => i.DefinitionId).ShouldContain(DefaultWorkflows.Post);
        b.Select(i => i.DefinitionId).ShouldContain(DefaultWorkflows.Post);
        a.Select(i => i.Id).Intersect(b.Select(i => i.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ADryRunSaysTheStepsAndChangesNothing()
    {
        var replica = await ReplicaAsync();
        _platform.ShiftUnit = _desk;

        var result = await DryRunAsync(replica, new DryRunRequest(42, _section));

        result.ShouldNotBeNull().Problems.ShouldBeEmpty();
        var main = result.Workflow.ShouldNotBeNull();
        (main.DefinitionId, main.Version).ShouldBe((DefaultWorkflows.Post, 2)); // no version given: the latest
        main.Steps.Select(s => s.ActivityId).ShouldBe(["ensure", "produce", "desk", "publication", "announce"]);
        main.Steps.Single(s => s.ActivityId == "publication").WouldWait.ShouldBeTrue();
        var sub = main.Steps.Single(s => s.ActivityId == "produce").Steps.ShouldNotBeNull();
        (sub.DefinitionId, sub.ContentId).ShouldBe((DefaultWorkflows.Content, _contentId));
        sub.Steps.Select(s => s.ActivityId).ShouldBe(["file", "work"]);
        sub.Steps[1].Description.ShouldContain(Writer);
        main.Steps.Single(s => s.ActivityId == "desk").WouldWait.ShouldBeTrue();

        (await replica.InstancesAsync(Tenants.A)).ShouldBeEmpty();
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
        _messenger.Sent.ShouldBeEmpty();
        // The only posts are NewsRoom's two questions, which change nothing.
        _platform.Calls.Where(c => !c.Contains(" GET ")).ShouldAllBe(c =>
            c.EndsWith("/api/newsroom/routing/route") || c.EndsWith("/api/newsroom/workload/query"));
        (Content.AssigneeId, Content.UnitId, Post.UnitId, _project.State)
            .ShouldBe((null, (Guid?)_section, null, ProjectStates.Approved));
    }

    [Fact]
    public async Task ADryRunStopsWhereThePostCannotBeFiled()
    {
        var replica = await ReplicaAsync();
        _platform.UnitCategories[_section] = ["sport"];

        var result = await DryRunAsync(replica, new DryRunRequest(42, _section, DefaultWorkflows.Post));

        var steps = result!.Workflow!.Steps;
        steps.Select(s => s.ActivityId).ShouldBe(["ensure", "refile"]);
        steps[^1].Outcome.ShouldBeNull();
    }

    [Fact]
    public async Task ADryRunReportsWhatKeepsAWorkflowFromRunning()
    {
        var replica = await ReplicaAsync();
        _platform.PostBinding = null;

        (await DryRunAsync(replica, new DryRunRequest(42, _section)))!.Problems.ShouldHaveSingleItem()
            .ShouldContain("No workflow is bound");
        (await DryRunAsync(replica, new DryRunRequest(42, _section, "nothing")))!.Problems.ShouldHaveSingleItem()
            .ShouldContain("nothing");
        (await DryRunAsync(replica, new DryRunRequest(42)))!.Problems.ShouldNotBeEmpty();
        (await DryRunAsync(replica, new DryRunRequest(999, _section))).ShouldBeNull();
    }

    private static Task<DryRunResult?> DryRunAsync(Replica replica, DryRunRequest request) =>
        replica.InTenantAsync(Tenants.A, (services, ct) => services.GetRequiredService<DryRunner>().RunAsync(request, ct));

    /// <summary>
    /// A released version of a code workflow never changes, since suspended instances resume on what the code builds
    /// now. If this fails, do not update the text for an existing version: add a new version beside it.
    /// </summary>
    [Theory]
    [InlineData(DefaultWorkflows.Post, 1, PostV1)]
    [InlineData(DefaultWorkflows.Post, 2, PostV2)]
    [InlineData(DefaultWorkflows.Content, 1, ContentV1)]
    public async Task AReleasedVersionKeepsItsGraph(string definitionId, int version, string expected)
    {
        var replica = await ReplicaAsync();

        var graph = await replica.InTenantAsync(Tenants.A, async (services, ct) =>
        {
            var found = await services.GetRequiredService<IWorkflowDefinitionService>().FindWorkflowGraphAsync(
                WorkflowDefinitionHandle.ByDefinitionId(definitionId, VersionOptions.SpecificVersion(version)), ct);
            return GraphText(found.ShouldNotBeNull().Workflow);
        });

        graph.ShouldBe(expected.ReplaceLineEndings("\n"));
    }

    private static string GraphText(global::Elsa.Workflows.Activities.Workflow workflow)
    {
        var root = (Flowchart)workflow.Root;
        var lines = new List<string> { $"{workflow.Identity.Id} {root.Id} start={root.Start?.Id}" };
        foreach (var activity in root.Activities)
            lines.Add($"{activity.Id} {activity.Type}");
        foreach (var c in root.Connections)
            lines.Add($"{c.Source.Activity.Id}.{c.Source.Port} -> {c.Target.Activity.Id}");
        return string.Join('\n', lines);
    }

    private const string PostV1 = """
        newsroom-post:v1 post start=ensure
        ensure SoftSquare.Newsroom.EnsurePostInUnitCategories
        refile SoftSquare.Newsroom.WaitForRefiling
        produce SoftSquare.Newsroom.ProduceContents
        desk SoftSquare.Newsroom.DeskReview
        publish SoftSquare.Newsroom.PublishPost
        refused SoftSquare.Newsroom.Notify
        announce SoftSquare.Newsroom.Notify
        ensure.Filed -> produce
        ensure.NeedsModeration -> refile
        refile.Refiled -> ensure
        produce.Produced -> desk
        produce.Refiled -> ensure
        desk.Approved -> publish
        desk.Returned -> produce
        desk.Refiled -> ensure
        publish.Published -> announce
        publish.Refused -> refused
        refused.Done -> desk
        """;

    private const string PostV2 = """
        newsroom-post:v2 post start=ensure
        ensure SoftSquare.Newsroom.EnsurePostInUnitCategories
        refile SoftSquare.Newsroom.WaitForRefiling
        produce SoftSquare.Newsroom.ProduceContents
        desk SoftSquare.Newsroom.DeskReview
        publication SoftSquare.Newsroom.WaitForPublication
        announce SoftSquare.Newsroom.Notify
        ensure.Filed -> produce
        ensure.NeedsModeration -> refile
        refile.Refiled -> ensure
        produce.Produced -> desk
        produce.Refiled -> ensure
        desk.Approved -> publication
        desk.Returned -> produce
        desk.Refiled -> ensure
        publication.Published -> announce
        publication.BackToDesk -> desk
        publication.Refiled -> ensure
        """;

    private const string ContentV1 = """
        newsroom-content:v1 content start=file
        file SoftSquare.Newsroom.FileContentInUnit
        work SoftSquare.Newsroom.WorkInShift
        file.Filed -> work
        file.Kept -> work
        """;
}
