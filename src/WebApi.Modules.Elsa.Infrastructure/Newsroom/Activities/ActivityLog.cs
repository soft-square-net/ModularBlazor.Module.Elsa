using Microsoft.Extensions.Logging;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Activities;

internal static class ActivityLog
{
    public static void LogRefusedClear(this ILogger logger, int postId, string reason) =>
        logger.LogWarning("Content did not clear the unit of post {PostId}: {Reason}", postId, reason);

    public static void LogRefusedAssignment(this ILogger logger, Guid contentId, int status, string reason) =>
        logger.LogWarning("Content did not assign content {ContentId} ({Status}): {Reason}", contentId, status, reason);

    public static void LogRefusedContentUnit(this ILogger logger, Guid contentId, Guid unitId, int status, string reason) =>
        logger.LogWarning("Content did not give content {ContentId} to unit {UnitId} ({Status}): {Reason}",
            contentId, unitId, status, reason);

    public static void LogRefusedDeskStep(this ILogger logger, string step, int postId, int status, string reason) =>
        logger.LogWarning("Content refused to {Step} post {PostId} ({Status}): {Reason}", step, postId, status, reason);

    public static void LogNobodyToTell(this ILogger logger, string instanceId) =>
        logger.LogWarning("Workflow instance {InstanceId} missed a deadline and found nobody to tell.", instanceId);
}
