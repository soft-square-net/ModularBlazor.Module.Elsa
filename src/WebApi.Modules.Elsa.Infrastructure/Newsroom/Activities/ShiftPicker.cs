using Elsa.Extensions;
using Elsa.Workflows;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

/// <summary>Someone who can take work now, and the running shift they are checked in to.</summary>
internal sealed record Picked(string UserId, int ShiftId);

internal static class ShiftPicker
{
    /// <summary>The role's holders who are checked in to one of the shifts.</summary>
    public static IReadOnlyList<Picked> Candidates(IReadOnlyList<ShiftSnapshot> running, string role, string? excluding) =>
        running.OrderBy(s => s.Id)
            .SelectMany(s => s.Rota
                .Where(m => string.Equals(m.RoleKey, role, StringComparison.OrdinalIgnoreCase) &&
                            m is { CheckedInAt: not null, CheckedOutAt: null } && m.UserId != excluding)
                .Select(m => new Picked(m.UserId, s.Id)))
            .DistinctBy(p => p.UserId)
            .ToArray();

    /// <summary>The candidate with the least open work, the first by id among equals; null when there is none.</summary>
    public static async Task<Picked?> LeastLoadedAsync(
        ActivityExecutionContext context, IReadOnlyList<ShiftSnapshot> running, string role, string? excluding,
        CancellationToken ct)
    {
        var candidates = Candidates(running, role, excluding);
        if (candidates.Count == 0) return null;

        var loads = await context.GetRequiredService<IShiftWorkload>()
            .OpenItemsAsync(candidates.Select(c => c.UserId).ToArray(), ct);
        return candidates.OrderBy(c => loads.GetValueOrDefault(c.UserId)).ThenBy(c => c.UserId, StringComparer.Ordinal)
            .First();
    }
}
