using System.Net;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

// What the activities read from and ask of the other modules: only the fields they use, copied from each module's API
// (consumers copy records, so no type is shared across the modules' load contexts). Every route goes through the gateway.

public sealed record PostSnapshot(
    int Id, int Version, string State, IReadOnlyList<string> Categories, Guid? UnitId, DateTimeOffset? ScheduledFor,
    string? OwnerId = null, string? AssignedShiftId = null, IReadOnlyList<PostContentSnapshot>? Contents = null,
    string? PostKind = null);

/// <summary>A content attached to a post. <paramref name="FieldKey"/> is its content kind.</summary>
public sealed record PostContentSnapshot(Guid ContentId, bool Required, string FieldKey, string Stage, bool Archived);

public sealed record ContentSnapshot(
    Guid Id, int Version, string Stage, string? AssigneeId, string? SectionId, Guid? UnitId, string? FieldKey = null);

public sealed record RotaMember(
    string UserId, string RoleKey, bool IsLead, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt);

public sealed record ShiftSnapshot(
    int Id, Guid? UnitId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool Published, bool Running, bool Ended,
    IReadOnlyList<RotaMember> Rota);

public sealed record UnitSnapshot(Guid Id, Guid? ParentId, string Path, IReadOnlyList<string> Tags, IReadOnlyList<string> Categories);

public sealed record UnitMember(string Subject, string? PositionKey);

/// <summary>Content's public API (/api/content).</summary>
internal sealed class ContentModuleApi(PlatformApi api)
{
    public Task<PostSnapshot?> PostAsync(int id, CancellationToken ct) => api.FindAsync<PostSnapshot>($"/api/content/posts/{id}", ct);

    public Task<ContentSnapshot?> ContentAsync(Guid id, CancellationToken ct) =>
        api.FindAsync<ContentSnapshot>($"/api/content/contents/{id:D}", ct);

    /// <param name="unitId">Null clears the unit.</param>
    public Task<PlatformAnswer> SetPostUnitAsync(int id, int version, Guid? unitId, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Put, $"/api/content/posts/{id}/unit", new { version, unitId }, ct);

    public Task<PlatformAnswer> AssignAsync(ContentSnapshot content, string assigneeId, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"/api/content/contents/{content.Id:D}/assign",
            new { version = content.Version, sectionId = content.SectionId, assigneeId }, ct);

    public Task<PlatformAnswer> SetContentUnitAsync(Guid id, int version, Guid unitId, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Put, $"/api/content/contents/{id:D}/unit", new { version, unitId }, ct);

    public Task<PlatformAnswer> AcceptAsync(int id, int version, string shiftId, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"/api/content/posts/{id}/accept", new { version, shiftId }, ct);

    public Task<PlatformAnswer> AssignPostAsync(int id, int version, string assigneeId, string shiftId, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"/api/content/posts/{id}/assign", new { version, assigneeId, shiftId }, ct);

    /// <summary>Handover: a review goes back to the queue.</summary>
    public Task<PlatformAnswer> TransferAsync(int id, int version, string fromShift, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"/api/content/posts/{id}/transfer", new { version, fromShift }, ct);

    /// <summary>Publishes every scheduled post of the tenant that is due.</summary>
    public Task<PlatformAnswer> PublishDueAsync(CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, "/api/content/posts/publish-due", null, ct);

    public Task<PlatformAnswer> PublishAsync(int id, int version, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"/api/content/posts/{id}/publish", new { version }, ct);

    public Task<PlatformAnswer> ScheduleAsync(int id, int version, DateTimeOffset publishAt, CancellationToken ct) =>
        api.SendAsync(HttpMethod.Post, $"/api/content/posts/{id}/schedule", new { version, publishAt }, ct);
}

/// <summary>Shifts' public API (/api/shifts).</summary>
internal sealed class ShiftsModuleApi(PlatformApi api, TimeProvider clock)
{
    /// <summary>The unit's shifts that run now.</summary>
    public async Task<IReadOnlyList<ShiftSnapshot>> RunningAsync(Guid unitId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var from = Uri.EscapeDataString(now.AddDays(-2).ToString("O"));
        var to = Uri.EscapeDataString(now.AddDays(1).ToString("O"));
        var shifts = await api.FindAsync<List<ShiftSnapshot>>($"/api/shifts/shifts?from={from}&to={to}", ct) ?? [];
        return shifts.Where(s => s.UnitId == unitId && s.Published && s.Running && !s.Ended).ToArray();
    }

    /// <summary>Whether that shift is over: checked out as a whole, ended by the clock, or gone.</summary>
    public async Task<bool> HasEndedAsync(int shiftId, CancellationToken ct) =>
        await api.FindAsync<ShiftSnapshot>($"/api/shifts/shifts/{shiftId}", ct) is not { Ended: false };
}

/// <summary>Organization's directory of the tenant's units (/api/organization/local).</summary>
internal sealed class OrganizationModuleApi(PlatformApi api)
{
    public async Task<IReadOnlyList<UnitSnapshot>> UnitsAsync(CancellationToken ct) =>
        await api.FindAsync<List<UnitSnapshot>>("/api/organization/local/units", ct)
        ?? throw new PlatformUnavailableException("Organization's unit directory is not served.");

    public Task<UnitSnapshot?> UnitAsync(Guid id, CancellationToken ct) =>
        api.FindAsync<UnitSnapshot>($"/api/organization/local/units/{id:D}", ct);

    /// <summary>The post categories the unit serves ("*" is all of them); null when the unit is unknown.</summary>
    public async Task<IReadOnlyList<string>?> CategoriesAsync(Guid unitId, CancellationToken ct) =>
        await api.FindAsync<List<string>>($"/api/organization/local/units/{unitId:D}/categories", ct);

    public async Task<IReadOnlyList<UnitMember>> MembersAsync(Guid unitId, CancellationToken ct) =>
        await api.FindAsync<List<UnitMember>>($"/api/organization/local/units/{unitId:D}/members", ct) ?? [];
}
