using System.Text.Json;
using soft.square.net.WebApi.Contracts.Messaging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;

namespace soft.square.net.WebApi.Modules.Elsa.UnitTests;

public class NewsroomEventSubjects
{
    private const string Id = "0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10";

    private static IntegrationEnvelope Envelope(string type, string subject, string data) =>
        new(Guid.NewGuid(), type, "tenant-a", subject, DateTimeOffset.UtcNow, "c", "s",
            JsonDocument.Parse(data).RootElement);

    [Theory]
    [InlineData("content.work-item-state-changed.v1", "content:content:" + Id, "{}", "content:" + Id)]
    [InlineData("content.post-categories-changed.v1", "post:42", "{}", "post:42")]
    [InlineData("content.post-state-changed.v1", "post:42", "{}", "post:42")]
    [InlineData("newsroom.project-state-changed.v1", "project:" + Id, """{"projectId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10"}""", "project:" + Id)]
    [InlineData("newsroom.project-reapproved.v1", "project:" + Id, """{"projectId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10"}""", "project:" + Id)]
    [InlineData("shifts.checked-in.v1", "shift:7", """{"shiftId":7,"unitId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10"}""", "unit:" + Id)]
    [InlineData("shifts.published.v1", "shift:7", """{"shift":{"unitId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10"}}""", "unit:" + Id)]
    public void AnEventSignalsWhatItIsAbout(string type, string subject, string data, string expected) =>
        Subjects.Of(Envelope(type, subject, data)).ShouldBe([expected]);

    [Theory]
    [InlineData("shifts.checked-in.v1", "shift:7", """{"shiftId":7}""")]
    [InlineData("content.post-updated.v1", "post:abc", "{}")]
    [InlineData("org.unit-created.v1", "unit:1", "{}")]
    public void AnEventWithNothingToWaitOnSignalsNothing(string type, string subject, string data) =>
        Subjects.Of(Envelope(type, subject, data)).ShouldBeEmpty();

    [Fact]
    public void AnApprovedProjectIsReadAsNewsRoomSendsIt()
    {
        var data = JsonDocument.Parse("""
            {"projectId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10","proposalId":3,"version":2,"approvalNumber":1,
             "postId":42,"postPublicId":"abc","postKind":"post.news","sectionId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a11",
             "sectionPath":"/org/desk","categories":["1","2"],"title":"A story","authorId":"u1",
             "proposalUnitId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a12","proposalUnitPath":"/org/x","approvedBy":"u2",
             "approvedAt":"2026-11-28T10:00:00+00:00","note":null}
            """).RootElement;

        var project = ProjectApproved.Read(data);

        (project.ProjectId, project.PostId, project.ApprovalNumber).ShouldBe((Guid.Parse(Id), 42, 1));
        project.SectionId.ShouldBe(Guid.Parse("0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a11"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"projectId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a10","approvalNumber":1,"sectionId":"0199c1f4-7b1e-7c3a-9a53-2f0d6f6c1a11"}""")]
    public void AnApprovedProjectThatLacksWhatAWorkflowNeedsIsRefused(string data) =>
        Should.Throw<JsonException>(() => ProjectApproved.Read(JsonDocument.Parse(data).RootElement));

    [Fact]
    public void TheManifestSubscribesToEveryEventAHandlerIsRegisteredFor() =>
        new ElsaModule().Manifest.Subscriptions.ShouldBe(NewsroomEventTypes.Subscriptions);
}
