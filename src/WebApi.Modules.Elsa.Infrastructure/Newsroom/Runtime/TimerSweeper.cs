using Elsa.Extensions;
using Elsa.Scheduling;
using Elsa.Scheduling.Bookmarks;
using Elsa.Workflows;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Contracts.Tenancy;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

/// <summary>
/// Elsa keeps a timer in the memory of the replica that set it, and reads the stored ones again only when a replica
/// starts. A timer whose replica stopped would wait until the next start, so the overdue ones are resumed from the
/// database. Running one twice does no harm: the first run removes the bookmark, and a run of a bookmark that is gone
/// does nothing.
/// </summary>
internal sealed class OverdueTimers(
    IBookmarkStore bookmarks, IWorkflowRuntime runtime, TimeProvider clock, IConfiguration settings,
    ILogger<OverdueTimers> logger)
{
    public const string GraceSetting = "Elsa:Newsroom:TimerSweepGrace";

    /// <summary>Resumes the current tenant's timers that are overdue by more than the grace. Returns how many.</summary>
    public async Task<int> ResumeAsync(CancellationToken ct)
    {
        // The grace leaves the timer's own replica time to fire it.
        var due = clock.GetUtcNow() - settings.GetValue(GraceSetting, TimeSpan.FromSeconds(30));
        var delays = await bookmarks.FindManyAsync(new BookmarkFilter { Name = SchedulingStimulusNames.Delay }, ct);
        var resumed = 0;
        foreach (var bookmark in delays.Where(b => b.GetPayload<DelayPayload>().ResumeAt < due))
        {
            try
            {
                var client = await runtime.CreateClientAsync(bookmark.WorkflowInstanceId, ct);
                var run = await client.RunInstanceAsync(new RunWorkflowInstanceRequest { BookmarkId = bookmark.Id }, ct);
                // A finished instance runs nothing, so a timer it left behind (a cancel through Elsa's API) would stay.
                if (run.Status == WorkflowStatus.Finished)
                    await bookmarks.DeleteAsync(new BookmarkFilter { BookmarkId = bookmark.Id }, ct);
                resumed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "The overdue timer of workflow instance {InstanceId} could not be resumed.",
                    bookmark.WorkflowInstanceId);
            }
        }

        return resumed;
    }
}

/// <summary>Runs <see cref="OverdueTimers"/> for each active tenant, as one of the module's hosted services.</summary>
internal sealed class TimerSweeperService(
    ElsaEngine engine, ITenantDirectory directory, IConfiguration settings, ILogger<TimerSweeperService> logger)
    : BackgroundService
{
    public const string IntervalSetting = "Elsa:Newsroom:TimerSweepInterval";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.GetValue(IntervalSetting, TimeSpan.FromMinutes(1)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var tenant in directory.All.Where(t => t.IsActive).ToArray())
                {
                    try
                    {
                        await engine.RunInTenantAsync(tenant.Id,
                            (services, ct) => ((OverdueTimers)services.GetService(typeof(OverdueTimers))!).ResumeAsync(ct),
                            stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "The timer sweep for tenant {Tenant} failed.", tenant.Id);
                    }
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
