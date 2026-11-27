using System.Runtime.ExceptionServices;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure;

/// <summary>An item of Shifts' ended list, as far as the handover needs it.</summary>
internal readonly record struct EndedShift(int Id, DateTimeOffset EndedAt);

/// <summary>
/// One tenant's place in Shifts' list of ended shifts. It is kept in memory: a new process starts again at
/// <paramref name="start"/> (now minus the lookback) and may repeat handovers, which Content treats as idempotent.
/// </summary>
internal sealed class EndedShiftCursor(DateTimeOffset start)
{
    /// <summary>The largest page Shifts returns.</summary>
    public const int PageSize = 500;

    /// <summary>
    /// How far each read goes back behind the newest end seen. A shift can be saved after one with a later
    /// <c>endedAt</c> was already read (the clock saves its shifts one by one, all with the tick's time, while people
    /// check others out), so reading from exactly the newest end could skip it.
    /// </summary>
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private readonly Dictionary<int, DateTimeOffset> _handled = [];
    private DateTimeOffset? _position; // the newest end seen, held back at the oldest one whose handover failed

    /// <summary>The <c>since</c> of the next read (inclusive).</summary>
    public DateTimeOffset Since => _position - Overlap ?? start;

    /// <summary>How many handled shifts are remembered to skip them: only those the next read can still return.</summary>
    internal int Remembered => _handled.Count;

    /// <summary>
    /// Reads every shift ended from <see cref="Since"/> on and hands over each one not handled yet. A failed handover
    /// does not stop the others; the cursor stays at it so the next call retries it, and the first failure is rethrown.
    /// </summary>
    /// <param name="read">
    /// One page of at most <see cref="PageSize"/> shifts ended at or after the time, ordered by end then id; null when
    /// Shifts has no ended list.
    /// </param>
    /// <returns>False when <paramref name="read"/> returned null.</returns>
    public async Task<bool> DrainAsync(
        Func<DateTimeOffset, CancellationToken, Task<IReadOnlyList<EndedShift>?>> read,
        Func<int, CancellationToken, Task> handOver,
        CancellationToken cancellationToken)
    {
        var since = Since;
        var newest = _position;
        DateTimeOffset? oldestFailed = null;
        ExceptionDispatchInfo? failure = null;

        while (true)
        {
            var page = await read(since, cancellationToken);
            if (page is null) return false;

            foreach (var shift in page)
            {
                if (newest is null || shift.EndedAt > newest) newest = shift.EndedAt;
                if (_handled.ContainsKey(shift.Id)) continue;
                try
                {
                    await handOver(shift.Id, cancellationToken);
                    _handled[shift.Id] = shift.EndedAt;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                    if (oldestFailed is null || shift.EndedAt < oldestFailed) oldestFailed = shift.EndedAt;
                }
            }

            if (page.Count < PageSize) break;
            if (page[^1].EndedAt <= since)
            {
                // The list pages by time only, so a full page that ends where it started cannot be passed.
                failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    $"{PageSize} or more shifts ended at {since:O}; later ones cannot be listed."));
                break;
            }
            since = page[^1].EndedAt;
        }

        _position = oldestFailed ?? newest;
        var next = Since;
        foreach (var id in _handled.Where(h => h.Value < next).Select(h => h.Key).ToArray()) _handled.Remove(id);

        failure?.Throw();
        return true;
    }
}
