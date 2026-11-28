using System.Collections.Concurrent;
using Elsa.Scheduling;
using Elsa.Scheduling.Services;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

/// <summary>
/// Elsa's in-memory scheduler, which can cancel everything it holds. Elsa 3.9.0 leaves its timers running when its
/// container is disposed, and one that fires then throws from a timer callback, which ends the process.
/// </summary>
internal sealed class StoppableScheduler(LocalScheduler inner) : IScheduler
{
    private readonly ConcurrentDictionary<string, byte> _names = new();

    public ValueTask ScheduleAsync(string name, ITask task, ISchedule schedule, CancellationToken cancellationToken = default) =>
        ScheduleAsync(name, task, schedule, null, cancellationToken);

    public ValueTask ScheduleAsync(string name, ITask task, ISchedule schedule, IEnumerable<string>? keys = null,
        CancellationToken cancellationToken = default)
    {
        _names[name] = 0;
        return inner.ScheduleAsync(name, task, schedule, keys, cancellationToken);
    }

    public ValueTask ClearScheduleAsync(string name, CancellationToken cancellationToken = default)
    {
        _names.TryRemove(name, out _);
        return inner.ClearScheduleAsync(name, cancellationToken);
    }

    public ValueTask ClearScheduleAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
        inner.ClearScheduleAsync(keys, cancellationToken);

    public async Task StopAllAsync()
    {
        foreach (var name in _names.Keys) await ClearScheduleAsync(name);
    }
}
