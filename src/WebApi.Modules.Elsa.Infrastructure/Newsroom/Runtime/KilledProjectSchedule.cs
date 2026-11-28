using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

/// <summary>
/// A killed project's post must not go out: Content publishes a due post by itself, so its schedule is taken back. It
/// does not matter who scheduled it, or whether the project ran a workflow.
/// </summary>
internal sealed class KilledProjectSchedule(
    INewsRoomProjects projects, ContentModuleApi content, ILogger<KilledProjectSchedule> logger)
{
    /// <summary>
    /// Unschedules the project's post if it is scheduled. Safe to repeat. It throws while the schedule may still
    /// stand, so that the kill's event is delivered again.
    /// </summary>
    public async Task UnscheduleAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projects.GetAsync(projectId, ct);
        if (project?.PostId is not { } postId) return;
        if (await content.PostAsync(postId, ct) is not { State: "Scheduled" } post) return;

        var answer = await content.UnscheduleAsync(postId, post.Version, ct);
        if (answer.IsSuccess)
        {
            logger.LogInformation("Unscheduled post {PostId}: its project {ProjectId} was killed.", postId, projectId);
            return;
        }

        // The post may have changed since it was read, or been published or removed.
        var current = await content.PostAsync(postId, ct);
        if (current is null or { State: not "Scheduled" })
        {
            if (current is { State: "Published" })
                logger.LogWarning(
                    "Post {PostId} of the killed project {ProjectId} was published before it could be unscheduled.",
                    postId, projectId);
            return;
        }

        throw new PlatformUnavailableException(
            $"Content did not unschedule post {postId} of the killed project {projectId} " +
            $"({(int)answer.Status}): {answer.Reason()}");
    }
}
