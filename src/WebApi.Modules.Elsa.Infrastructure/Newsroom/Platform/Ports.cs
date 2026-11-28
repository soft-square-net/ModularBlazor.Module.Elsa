using System.Net;
using System.Text.Json;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

// The small interfaces to NewsRoom: bindings, routing rules, the load per person and the project's state.

/// <summary>A definition to run; <paramref name="Version"/> null means its latest published version.</summary>
public sealed record WorkflowChoice(string DefinitionId, int? Version = null);

/// <summary>NewsRoom's workflow bindings, inherited up the unit tree.</summary>
public interface IWorkflowBindings
{
    /// <summary>The main workflow of a project; null when no unit on its path binds its post kind (a setup error).</summary>
    Task<WorkflowChoice?> ForProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>The main workflow a post kind has in a unit; null when none is bound. For a dry run.</summary>
    Task<WorkflowChoice?> ForPostKindAsync(string postKind, Guid unitId, CancellationToken ct);

    /// <summary>The sub-workflow of a content kind in a unit; null when none is bound.</summary>
    Task<WorkflowChoice?> ForContentAsync(string contentKind, Guid unitId, CancellationToken ct);
}

internal sealed class HttpWorkflowBindings(PlatformApi api) : IWorkflowBindings
{
    private sealed record Resolution(string DefinitionId, int? DefinitionVersion);

    public Task<WorkflowChoice?> ForProjectAsync(Guid projectId, CancellationToken ct) =>
        ResolveAsync($"/api/newsroom/projects/{projectId:D}/workflow", ct);

    public Task<WorkflowChoice?> ForPostKindAsync(string postKind, Guid unitId, CancellationToken ct) =>
        BoundAsync("post", postKind, unitId, ct);

    public Task<WorkflowChoice?> ForContentAsync(string contentKind, Guid unitId, CancellationToken ct) =>
        BoundAsync("content", contentKind, unitId, ct);

    private Task<WorkflowChoice?> BoundAsync(string kindType, string kind, Guid unitId, CancellationToken ct) =>
        ResolveAsync(
            $"/api/newsroom/bindings/resolve?kindType={kindType}&kind={Uri.EscapeDataString(kind)}&unitId={unitId:D}", ct);

    private async Task<WorkflowChoice?> ResolveAsync(string path, CancellationToken ct)
    {
        var answer = await api.SendAsync(HttpMethod.Get, path, null, ct);
        if (answer.Status == HttpStatusCode.Conflict) return null; // no binding up the path
        if (!answer.IsSuccess)
            throw new PlatformUnavailableException($"GET {path} answered {(int)answer.Status}: {answer.Reason()}");
        var resolution = answer.Read<Resolution>();
        return new WorkflowChoice(resolution.DefinitionId, resolution.DefinitionVersion);
    }
}

public sealed record RouteQuery(string ContentKind, IReadOnlyCollection<string> Categories);

public interface IUnitRouter
{
    /// <summary>The unit that should produce the content kind, or null when no rule leads to one.</summary>
    Task<Guid?> RouteAsync(RouteQuery query, CancellationToken ct);
}

/// <summary>NewsRoom's routing rules.</summary>
internal sealed class HttpUnitRouter(PlatformApi api) : IUnitRouter
{
    private const int CategoryLimit = 50;

    private sealed record Route(Guid? UnitId);

    public async Task<Guid?> RouteAsync(RouteQuery query, CancellationToken ct)
    {
        const string path = "/api/newsroom/routing/route";
        var answer = await api.SendAsync(HttpMethod.Post, path,
            new { contentKind = query.ContentKind, categories = query.Categories.Take(CategoryLimit) }, ct);
        if (!answer.IsSuccess)
            throw new PlatformUnavailableException($"POST {path} answered {(int)answer.Status}: {answer.Reason()}");
        return answer.Read<Route>().UnitId;
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

/// <summary>How much open work people have.</summary>
public interface IShiftWorkload
{
    /// <summary>Each person's open items; someone it knows nothing about may be left out.</summary>
    Task<IReadOnlyDictionary<string, int>> OpenItemsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct);
}

/// <summary>NewsRoom's read model of the load per person.</summary>
internal sealed class HttpShiftWorkload(PlatformApi api) : IShiftWorkload
{
    private const int Limit = 200;

    private sealed record Load(string UserId, int OpenItems);

    private sealed record Answer(IReadOnlyList<Load> Items);

    public async Task<IReadOnlyDictionary<string, int>> OpenItemsAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        const string path = "/api/newsroom/workload/query";
        var loads = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var batch in userIds.Distinct().Chunk(Limit))
        {
            var answer = await api.SendAsync(HttpMethod.Post, path, new { userIds = batch }, ct);
            if (!answer.IsSuccess)
                throw new PlatformUnavailableException($"POST {path} answered {(int)answer.Status}: {answer.Reason()}");
            foreach (var item in answer.Read<Answer>().Items) loads[item.UserId] = item.OpenItems;
        }

        return loads;
    }
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
public sealed record ProjectSnapshot(
    Guid Id, string State, Guid? SectionId, int? PostId, int ApprovalNumber, string? ProposalAuthorId = null,
    string? PostKind = null);

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
