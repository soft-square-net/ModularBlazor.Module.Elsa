using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Models;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.IntegrationTests;

[Collection(EngineTests.Collection)]
public class NewsroomWorkflowRuns(PostgresServer server) : IAsyncLifetime
{
    private readonly FakePlatform _platform = new();
    private readonly RecordingMessenger _messenger = new();
    private readonly List<Replica> _replicas = [];
    private readonly Guid _section = Guid.NewGuid();
    private readonly Guid _contentId = Guid.NewGuid();
    private FakeProject _project = null!;
    private string _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeContent Content => _platform.Contents[_contentId];

    public async ValueTask InitializeAsync()
    {
        _database = await server.NewDatabaseAsync(Ct);
        _project = new FakeProject { Id = Guid.NewGuid(), SectionId = _section, PostId = 42 };
        _platform.Projects[_project.Id] = _project;
        _platform.Posts[42] = new FakePost { Id = 42 };
        _platform.Contents[_contentId] = new FakeContent { Id = _contentId, UnitId = _section };
        _platform.UnitCategories[_section] = ["news"];
        _platform.ShiftUnit = _section;
        _platform.Rota.Add(new RotaMember("lead", "editor", true, DateTimeOffset.UtcNow, null));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var replica in _replicas) await replica.DisposeAsync();
    }

    private async Task<Replica> ReplicaAsync(Dictionary<string, string?>? settings = null)
    {
        var replica = await Replica.StartAsync(_database, _platform, _messenger, settings);
        _replicas.Add(replica);
        return replica;
    }

    private WaitForStage WaitForAccepted() => new() { ContentId = new(_contentId.ToString()) };

    private Deadline DeadlineIn(TimeSpan time) =>
        new() { ContentId = new(_contentId.ToString()), DueIn = new Input<TimeSpan?>(time) };

    private static Sequence Steps(params IActivity[] steps) => new() { Activities = [.. steps] };

    private Task ContentChangedAsync(Replica replica, string tenant = Tenants.A) => replica.DeliverAsync(
        tenant, "content.work-item-state-changed.v1", $"content:content:{_contentId:D}", new { });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("nobody")]
    [InlineData(Tenants.Suspended)]
    public async Task ElsaRunsForAKnownActiveTenantOnly(string? tenant)
    {
        var replica = await ReplicaAsync();
        var ran = false;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            replica.Engine.RunInTenantAsync(tenant, (_, _) => Task.FromResult(ran = true), Ct));

        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task AnApprovalDeliveredManyTimesToTwoReplicasStartsOneWorkflow()
    {
        var first = await ReplicaAsync();
        var second = await ReplicaAsync();
        await first.PublishAsync(Tenants.A, Steps(new EnsurePostInUnitCategories(), WaitForAccepted()));

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => (i % 2 == 0 ? first : second).ApproveAsync(Tenants.A, _project)));

        (await first.InstancesAsync(Tenants.A)).Count.ShouldBe(1);
        (await first.InstancesAsync(Tenants.B)).ShouldBeEmpty();
        _platform.Count("PUT /api/content/posts/42/unit").ShouldBe(1);
        _platform.Count("POST /api/newsroom").ShouldBe(1);
        _project.State.ShouldBe(ProjectStates.InProduction);
        _platform.Posts[42].UnitId.ShouldBe(_section);
        _platform.Calls.ShouldAllBe(call => call.StartsWith(Tenants.A + " "));
    }

    [Fact]
    public async Task AnEventThatCameBeforeTheWorkflowWaitedIsNotNeeded()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(WaitForAccepted()));
        Content.Stage = "accepted";
        await ContentChangedAsync(replica); // nothing waits yet

        await replica.ApproveAsync(Tenants.A, _project);

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 10);
    }

    [Fact]
    public async Task AChangeJustBeforeTheFirstSaveIsSeenByTheSecondLook()
    {
        // No event at all: only the look an activity takes shortly after it starts to wait.
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(WaitForAccepted()));
        await replica.ApproveAsync(Tenants.A, _project);

        _platform.Change(() => Content.Stage = "accepted");

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 10);
    }

    [Fact]
    public async Task ASignalKeepsItsBookmarkAndTheOtherReplicaCanResume()
    {
        var first = await ReplicaAsync();
        var second = await ReplicaAsync();
        await first.PublishAsync(Tenants.A, Steps(WaitForAccepted()));
        await first.ApproveAsync(Tenants.A, _project);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // past the second look
        // One for the content, one for the project: the workflow ends a wait whose project ended.
        var signals = (await first.BookmarksAsync(Tenants.A)).Where(b => b.Name == "Newsroom.Signal").Select(b => b.Id).Order().ToArray();
        signals.Length.ShouldBe(2);
        var looks = _platform.Count($"/contents/{_contentId:D}");

        await ContentChangedAsync(second); // nothing is accepted yet

        _platform.Count($"/contents/{_contentId:D}").ShouldBe(looks + 1);
        var bookmarks = await first.BookmarksAsync(Tenants.A);
        bookmarks.Where(b => b.Name == "Newsroom.Signal").Select(b => b.Id).Order().ShouldBe(signals);
        bookmarks.Count.ShouldBe(3); // that one, the project's, and one timer

        _platform.Change(() => Content.Stage = "accepted");
        await Task.WhenAll(ContentChangedAsync(first), ContentChangedAsync(second));

        await first.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 5);
        (await first.BookmarksAsync(Tenants.A)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASignalOfAnotherTenantResumesNothing()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(WaitForAccepted()));
        await replica.ApproveAsync(Tenants.A, _project);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        _platform.Change(() => Content.Stage = "accepted");

        await ContentChangedAsync(replica, Tenants.B);
        await Task.Delay(500, Ct);

        (await replica.InstancesAsync(Tenants.A)).Single().SubStatus.ShouldBe(WorkflowSubStatus.Suspended);
        _platform.Calls.ShouldNotContain(c => c.StartsWith(Tenants.B + " "));
    }

    [Fact]
    public async Task AMissedDeadlineTellsTheShiftLeadOfTheSameTenant()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(DeadlineIn(TimeSpan.FromSeconds(4))));

        await replica.ApproveAsync(Tenants.A, _project);

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 20);
        var message = _messenger.Sent.Single();
        (message.Tenant, message.Target, message.Type).ShouldBe((Tenants.A, "lead", Deadline.MissedNotification));
        _platform.Calls.ShouldAllBe(call => call.StartsWith(Tenants.A + " "));
    }

    [Fact]
    public async Task ADeadlineOutlivesARestart()
    {
        var first = await ReplicaAsync();
        await first.PublishAsync(Tenants.A, Steps(DeadlineIn(TimeSpan.FromSeconds(8))));
        await first.ApproveAsync(Tenants.A, _project);
        await first.DisposeAsync();
        _messenger.Sent.ShouldBeEmpty();

        var second = await ReplicaAsync();

        await second.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 30);
        _messenger.Sent.Single().Tenant.ShouldBe(Tenants.A);
    }

    [Fact]
    public async Task TheSweepRunsTheTimerOfAReplicaThatStopped()
    {
        var running = await ReplicaAsync(); // up before the timer exists, so it has not scheduled it
        var stopping = await ReplicaAsync();
        await running.PublishAsync(Tenants.A, Steps(DeadlineIn(TimeSpan.FromSeconds(5))));
        await stopping.ApproveAsync(Tenants.A, _project);
        await stopping.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(7), Ct);
        (await running.InstancesAsync(Tenants.A)).Single().SubStatus.ShouldBe(WorkflowSubStatus.Suspended);

        (await running.SweepAsync(Tenants.B)).ShouldBe(0);
        (await running.SweepAsync(Tenants.A)).ShouldBe(1);

        await running.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 5);
        _messenger.Sent.Single().Tenant.ShouldBe(Tenants.A);
    }

    [Fact]
    public async Task ATimerLeftOnAReplicaDoesNothingOnceAnotherReplicaMovedOn()
    {
        var first = await ReplicaAsync();
        var second = await ReplicaAsync();
        await first.PublishAsync(Tenants.A, Steps(DeadlineIn(TimeSpan.FromSeconds(6)), WaitForAccepted()));
        await first.ApproveAsync(Tenants.A, _project); // first holds the deadline's timer
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        _platform.Change(() => Content.Stage = "submitted");
        _platform.Change(() => Content.Stage = "accepted");
        await ContentChangedAsync(second); // second meets the deadline and finishes the workflow
        await second.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 5);
        var calls = _platform.Calls.Count;

        await Task.Delay(TimeSpan.FromSeconds(6), Ct); // first's timers fire for bookmarks that are gone

        _platform.Calls.Count.ShouldBe(calls);
        _messenger.Sent.ShouldBeEmpty();
        (await first.InstancesAsync(Tenants.A)).Single().SubStatus.ShouldBe(WorkflowSubStatus.Finished);
    }

    [Fact]
    public async Task AModuleThatDoesNotAnswerMakesTheWorkflowWaitNotFail()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(WaitForAccepted()));
        _platform.Change(() => Content.Stage = "accepted");
        _platform.Down = true;

        // With NewsRoom down the start itself fails, and the host delivers the event again.
        await Should.ThrowAsync<PlatformUnavailableException>(() => replica.ApproveAsync(Tenants.A, _project));
        (await replica.InstancesAsync(Tenants.A)).ShouldBeEmpty();

        _platform.Down = false;
        _platform.Contents.Remove(_contentId, out var content);
        await replica.ApproveAsync(Tenants.A, _project);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        (await replica.InstancesAsync(Tenants.A)).Single().SubStatus.ShouldBe(WorkflowSubStatus.Suspended);

        _platform.Change(() => _platform.Contents[_contentId] = content!);

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 10);
    }

    [Fact]
    public async Task APostItsSectionDoesNotServeGoesBackToModerationAndIsCheckedAgain()
    {
        var replica = await ReplicaAsync();
        var other = Guid.NewGuid();
        _platform.UnitCategories[_section] = ["sport"];
        _platform.UnitCategories[other] = ["news"];
        _platform.Posts[42].UnitId = other; // the unit it had before: it must be cleared
        await replica.PublishAsync(Tenants.A,
            Steps(new EnsurePostInUnitCategories(), new WaitForRefiling(), new EnsurePostInUnitCategories(), WaitForAccepted()));

        await replica.ApproveAsync(Tenants.A, _project);

        _project.State.ShouldBe(ProjectStates.NeedsModeration);
        _platform.Posts[42].UnitId.ShouldBeNull();
        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Suspended);

        // The moderator changes the categories, then approves again.
        _platform.Change(() => { _platform.Posts[42].Categories = ["sport"]; _platform.Posts[42].Version++; });
        await replica.DeliverAsync(Tenants.A, "content.post-categories-changed.v1", "post:42", new { });
        _project.State.ShouldBe(ProjectStates.NeedsModeration);

        _platform.Change(() => { _project.State = ProjectStates.Approved; _project.ApprovalNumber = 2; });
        await replica.ApproveAsync(Tenants.A, _project, "newsroom.project-reapproved.v1");

        await Replica.UntilAsync(() => _project.State == ProjectStates.InProduction, "the project in production");
        _platform.Posts[42].UnitId.ShouldBe(_section);
        (await replica.InstancesAsync(Tenants.A)).Single().SubStatus.ShouldBe(WorkflowSubStatus.Suspended);
    }

    [Fact]
    public async Task TheLeastLoadedPersonOnShiftIsAssignedAfterTheManagersDelay()
    {
        var replica = await ReplicaAsync();
        _platform.Rota.Add(new RotaMember("bea", "writer", false, DateTimeOffset.UtcNow, null));
        _platform.Rota.Add(new RotaMember("adam", "writer", false, DateTimeOffset.UtcNow, null));
        _platform.Rota.Add(new RotaMember("away", "writer", false, null, null));
        await replica.PublishAsync(Tenants.A, Steps(new AssignInShift
        {
            ContentId = new(_contentId.ToString()), RoleKey = new("writer"),
            ManagerDelay = new Input<TimeSpan?>(TimeSpan.FromSeconds(3))
        }));

        await replica.ApproveAsync(Tenants.A, _project);
        Content.AssigneeId.ShouldBeNull();

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 15);
        Content.AssigneeId.ShouldBe("adam"); // equal loads: the first by id
    }

    [Fact]
    public async Task APostTheDeskApprovedIsPublishedAndAnnounced()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(
            new RouteToUnit { ContentKind = new("video") },
            new EnsurePostInUnitCategories(), new DeskReview(), new PublishPost(),
            new Notify { UserId = new("author"), Message = new("It is out.") }));
        _platform.Routes["video"] = _section;
        await replica.ApproveAsync(Tenants.A, _project);
        await Replica.UntilAsync(() => _project.State == ProjectStates.ReadyForDesk, "the project at the desk");
        _platform.Count("/publish").ShouldBe(0);

        _platform.Change(() => { _platform.Posts[42].State = "Approved"; _platform.Posts[42].Version++; });
        await replica.DeliverAsync(Tenants.A, "content.post-state-changed.v1", "post:42", new { });

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 10);
        _platform.Count("POST /api/content/posts/42/publish").ShouldBe(1);
        _project.State.ShouldBe(ProjectStates.Published);
        var message = _messenger.Sent.Single();
        (message.Tenant, message.Target).ShouldBe((Tenants.A, "author"));
        message.Payload.GetProperty("projectId").GetString().ShouldBe(_project.Id.ToString("D"));
    }

    [Fact]
    public async Task ARefusedPublicationIsAnOutcomeNotAFault()
    {
        var replica = await ReplicaAsync();
        _platform.PublishRefusal = "The post's fields are not valid.";
        _platform.Posts[42].State = "Approved";
        await replica.PublishAsync(Tenants.A, Steps(new PublishPost()));

        await replica.ApproveAsync(Tenants.A, _project);

        var instance = await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Finished, seconds: 10);
        instance.WorkflowState.Incidents.ShouldBeEmpty();
        _project.State.ShouldBe(ProjectStates.Approved);
    }

    [Fact]
    public async Task AKilledProjectCancelsItsWorkflowAndAKillThatCameFirstStartsNone()
    {
        var replica = await ReplicaAsync();
        await replica.PublishAsync(Tenants.A, Steps(WaitForAccepted()));
        await replica.ApproveAsync(Tenants.A, _project);
        _project.State = ProjectStates.Killed;
        var killed = new { projectId = _project.Id, state = "Killed" };

        await replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}", killed);
        await replica.DeliverAsync(Tenants.A, "newsroom.project-state-changed.v1", $"project:{_project.Id:D}", killed);

        await replica.WaitForAsync(Tenants.A, WorkflowSubStatus.Cancelled, seconds: 5);
        (await replica.BookmarksAsync(Tenants.A)).ShouldBeEmpty();

        var late = new FakeProject { Id = Guid.NewGuid(), SectionId = _section, PostId = 43, State = ProjectStates.Killed };
        _platform.Projects[late.Id] = late;
        await replica.ApproveAsync(Tenants.A, late);
        (await replica.InstancesAsync(Tenants.A)).Count.ShouldBe(1);
    }
}
