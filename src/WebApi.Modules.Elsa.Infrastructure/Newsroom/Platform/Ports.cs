using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

// The small interfaces behind which the parts that later steps build are kept: bindings and routing rules (N3), the
// load read model (N3), and NewsRoom's project state.

/// <summary>Which workflow definition runs an approved project. N3 replaces this with NewsRoom's bindings.</summary>
public interface IWorkflowBindings
{
    /// <summary>The bound definition id, or null when nothing is bound (a setup error).</summary>
    Task<string?> ResolveAsync(ProjectApproved project, CancellationToken ct);
}

/// <summary>One definition for every project, from the module setting <c>Elsa:Newsroom:ProjectWorkflow</c>.</summary>
internal sealed class SettingWorkflowBindings(IConfiguration settings) : IWorkflowBindings
{
    public const string Setting = "Elsa:Newsroom:ProjectWorkflow";

    public Task<string?> ResolveAsync(ProjectApproved project, CancellationToken ct) =>
        Task.FromResult(settings[Setting] is { Length: > 0 } definitionId ? definitionId : null);
}

/// <summary>"Contents of this kind are produced by this unit, or by a unit with this tag."</summary>
public sealed record RoutingRuleInput(string ContentKind, Guid? UnitId = null, string? UnitTag = null);

public sealed record RouteQuery(string ContentKind, IReadOnlyCollection<string> Categories, IReadOnlyList<RoutingRuleInput> Rules);

public interface IUnitRouter
{
    /// <summary>The unit that should produce the content kind, or null when no rule leads to one.</summary>
    Task<Guid?> RouteAsync(RouteQuery query, CancellationToken ct);
}

/// <summary>
/// Applies the rules given to the activity, the exact content kind before "*", in their order. A rule naming a unit is
/// taken as it is; a rule naming a tag takes the first unit with that tag, by path, that serves one of the categories
/// (any tagged unit when the post has none). N3 keeps the rules in NewsRoom.
/// </summary>
internal sealed class InputUnitRouter(OrganizationModuleApi organization) : IUnitRouter
{
    public async Task<Guid?> RouteAsync(RouteQuery query, CancellationToken ct)
    {
        var rules = query.Rules.Where(r => Matches(r.ContentKind, query.ContentKind))
            .Concat(query.Rules.Where(r => r.ContentKind == "*" && query.ContentKind != "*"));
        IReadOnlyList<UnitSnapshot>? units = null;
        foreach (var rule in rules)
        {
            if (rule.UnitId is { } id) return id;
            if (string.IsNullOrWhiteSpace(rule.UnitTag)) continue;

            units ??= (await organization.UnitsAsync(ct)).OrderBy(u => u.Path, StringComparer.Ordinal).ToArray();
            foreach (var unit in units.Where(u => u.Tags.Contains(rule.UnitTag, StringComparer.OrdinalIgnoreCase)))
            {
                if (query.Categories.Count == 0) return unit.Id;
                var served = await organization.CategoriesAsync(unit.Id, ct);
                if (served is not null && UnitCategories.Covers(served, query.Categories)) return unit.Id;
            }
        }

        return null;

        static bool Matches(string rule, string kind) => string.Equals(rule, kind, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The Section categories rule: a post belongs in a unit when the unit serves one of its categories, or all of them
/// ("*"). Content enforces the same rule; a copy until Organization.Contracts ships it.
/// </summary>
public static class UnitCategories
{
    public const string All = "*";

    public static bool Covers(IReadOnlyCollection<string> served, IReadOnlyCollection<string> categories) =>
        served.Contains(All) || categories.Any(c => served.Contains(c, StringComparer.Ordinal));
}

/// <summary>How much open work a person has. N3 replaces this with NewsRoom's read model.</summary>
public interface IShiftWorkload
{
    Task<int> OpenItemsAsync(string userId, CancellationToken ct);
}

/// <summary>Counts the person's contents that are not accepted yet, from Content's list.</summary>
internal sealed class ContentShiftWorkload(ContentModuleApi content) : IShiftWorkload
{
    public async Task<int> OpenItemsAsync(string userId, CancellationToken ct) =>
        (await content.AssignedToAsync(userId, ct)).Count(c => c.Stage != "accepted");
}

/// <summary>The states of a post project.</summary>
public static class ProjectStates
{
    public const string Approved = "Approved";
    public const string NeedsModeration = "NeedsModeration";
    public const string InProduction = "InProduction";
    public const string ReadyForDesk = "ReadyForDesk";
    public const string Scheduled = "Scheduled";
    public const string Published = "Published";
    public const string Killed = "Killed";

    public static bool Is(string state, string expected) => string.Equals(state, expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The part of NewsRoom's project that the workflows read.</summary>
/// <param name="SectionId">The Section the moderator chose, which the workflow files the post in.</param>
/// <param name="ApprovalNumber">1, and one more each time a moderator answers NeedsModeration.</param>
public sealed record ProjectSnapshot(Guid Id, string State, Guid? SectionId, int? PostId, int ApprovalNumber);

/// <summary>NewsRoom's project state API.</summary>
public interface INewsRoomProjects
{
    /// <summary>The project, or null when NewsRoom does not know it.</summary>
    Task<ProjectSnapshot?> GetAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Reports a state for one approval; a project that is in it already is left alone. False when NewsRoom refused
    /// because the project moved on (another approval, or a state this one cannot follow): read it again.
    /// </summary>
    /// <param name="reason">Required for NeedsModeration; the moderator reads it.</param>
    Task<bool> SetStateAsync(Guid projectId, string state, int approvalNumber, string? reason, CancellationToken ct);
}

/// <summary>NewsRoom through the gateway (/api/newsroom). This class is the only place that knows its routes.</summary>
internal sealed class HttpNewsRoomProjects(PlatformApi api) : INewsRoomProjects
{
    private const int ReasonLimit = 1000;

    public Task<ProjectSnapshot?> GetAsync(Guid projectId, CancellationToken ct) =>
        api.FindAsync<ProjectSnapshot>(Path(projectId), ct);

    public async Task<bool> SetStateAsync(Guid projectId, string state, int approvalNumber, string? reason, CancellationToken ct)
    {
        if (reason?.Length > ReasonLimit) reason = reason[..ReasonLimit];
        var answer = await api.SendAsync(HttpMethod.Post, Path(projectId) + "/state", new { state, approvalNumber, reason }, ct);
        if (answer.IsSuccess) return true;
        if (answer.Status == HttpStatusCode.Conflict) return false;

        throw new PlatformUnavailableException(
            $"NewsRoom did not move project {projectId} to {state} ({(int)answer.Status}): {answer.Reason()}");
    }

    private static string Path(Guid projectId) => $"/api/newsroom/projects/{projectId:D}";
}

/// <summary>Live notifications to the clients of the workflow's tenant. Best effort.</summary>
public interface ILiveMessenger
{
    Task ToUserAsync(string tenantId, string userId, string type, JsonElement payload, CancellationToken ct);

    /// <summary>To the unit at the end of the path and each of its ancestors.</summary>
    Task ToUnitAsync(string tenantId, string unitPath, string type, JsonElement payload, CancellationToken ct);
}
