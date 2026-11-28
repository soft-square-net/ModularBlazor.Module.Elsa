using System.Globalization;
using System.Text.Json;
using soft.square.net.WebApi.Contracts.Messaging;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;

// Integration events are JSON, and each consumer keeps its own copy of what it reads: no contracts package of another
// module is referenced.

/// <summary>
/// <c>newsroom.project-approved.v1</c>, a copy of NewsRoom.Contracts 1.0.0-preview.1: published once per project, when
/// it is approved and its Content draft exists. <c>project-reapproved</c> and <c>project-binding-available</c> carry the
/// same record.
/// </summary>
/// <param name="SectionId">The unit the post is to be produced in.</param>
public sealed record ProjectApproved(
    Guid ProjectId, int ProposalId, int Version, int ApprovalNumber,
    int PostId, string? PostPublicId, string? PostKind,
    Guid SectionId, string? SectionPath,
    IReadOnlyList<string>? Categories,
    string? Title, string? AuthorId, Guid ProposalUnitId, string? ProposalUnitPath,
    string? ApprovedBy, DateTimeOffset ApprovedAt, string? Note)
{
    /// <summary>Reads the event's data; throws when it lacks what a workflow cannot start without.</summary>
    public static ProjectApproved Read(JsonElement data)
    {
        var project = data.Deserialize<ProjectApproved>(JsonSerializerOptions.Web)
            ?? throw new JsonException("The approved project is empty.");
        if (project.ProjectId == Guid.Empty || project.PostId <= 0 || project.SectionId == Guid.Empty || project.ApprovalNumber <= 0)
            throw new JsonException("The approved project lacks its projectId, postId, sectionId or approvalNumber.");
        return project;
    }
}

internal static class NewsroomEventTypes
{
    public const string ProjectApproved = "newsroom.project-approved.v1";
    public const string ProjectReapproved = "newsroom.project-reapproved.v1";
    public const string ProjectStateChanged = "newsroom.project-state-changed.v1";
    public const string ProjectBindingAvailable = "newsroom.project-binding-available.v1";

    /// <summary>What the module subscribes to; the manifest and the handler registrations both use it.</summary>
    public static readonly IReadOnlyList<string> Subscriptions =
    [
        ProjectApproved,
        ProjectReapproved,
        ProjectStateChanged,
        ProjectBindingAvailable,
        "content.work-item-*.v1",
        "content.post-*.v1",
        "shifts.checked-in.v1",
        "shifts.checked-out.v1",
        "shifts.handover.v1",
        "shifts.ended.v1",
        "shifts.published.v1",
        "shifts.member-added.v1"
    ];
}

/// <summary>
/// What a workflow waits on. An event says only that something about a subject may have changed: the waiting activity
/// then reads the owning module's current state, so a signal that is repeated, late or early does no harm.
/// </summary>
internal static class Subjects
{
    public static string Post(int postId) => $"post:{postId.ToString(CultureInfo.InvariantCulture)}";
    public static string Content(Guid contentId) => $"content:{contentId:D}";
    public static string Project(Guid projectId) => $"project:{projectId:D}";
    public static string Unit(Guid unitId) => $"unit:{unitId:D}";

    /// <summary>The subjects an event is about; none when no activity waits on such an event.</summary>
    public static IReadOnlyList<string> Of(IntegrationEnvelope envelope)
    {
        var data = envelope.Data;
        if (envelope.Type.StartsWith("content.work-item-", StringComparison.Ordinal))
            // The subject is the work item's reference, "content:content:{id}".
            return System.Guid.TryParse(LastSegment(envelope.Subject), out var contentId) ? [Content(contentId)] : [];
        if (envelope.Type.StartsWith("content.post-", StringComparison.Ordinal))
            return int.TryParse(LastSegment(envelope.Subject), NumberStyles.Integer, CultureInfo.InvariantCulture, out var postId)
                ? [Post(postId)]
                : [];
        if (envelope.Type is NewsroomEventTypes.ProjectStateChanged or NewsroomEventTypes.ProjectReapproved)
            return EventData.Guid(data, "projectId") is { } projectId ? [Project(projectId)] : [];
        if (envelope.Type.StartsWith("shifts.", StringComparison.Ordinal))
        {
            // shifts.published carries a snapshot of the shift; the attendance events name the unit themselves.
            var shift = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("shift", out var snapshot) ? snapshot : data;
            return EventData.Guid(shift, "unitId") is { } unitId ? [Unit(unitId)] : [];
        }

        return [];

        static string LastSegment(string subject) => subject[(subject.LastIndexOf(':') + 1)..];
    }
}

internal static class EventData
{
    public static string? Text(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static System.Guid? Guid(JsonElement data, string name) =>
        System.Guid.TryParse(Text(data, name), out var id) ? id : null;
}
