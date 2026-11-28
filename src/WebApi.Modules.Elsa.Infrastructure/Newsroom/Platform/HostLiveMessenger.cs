using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Contracts.Live;
using soft.square.net.WebApi.Contracts.Tenancy;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;

/// <summary>
/// The host's live notifier, which is scoped to a tenant: each message goes out in a tenant scope of the module's
/// container. A message that cannot be sent is logged and dropped.
/// </summary>
internal sealed class HostLiveMessenger(IServiceProvider module, ILogger<HostLiveMessenger> logger) : ILiveMessenger
{
    public Task ToUserAsync(string tenantId, string userId, string type, JsonElement payload, CancellationToken ct) =>
        SendAsync(tenantId, notifier => notifier.PublishAsync(LiveTopics.User(userId), type, payload, null, ct));

    public Task ToUnitAsync(string tenantId, string unitPath, string type, JsonElement payload, CancellationToken ct) =>
        SendAsync(tenantId, notifier => notifier.PublishToUnitAsync(unitPath, type, payload, null, ct));

    private async Task SendAsync(string tenantId, Func<ILiveNotifier, Task> send)
    {
        try
        {
            await using var scope = module.CreateTenantScope(tenantId);
            if (scope.ServiceProvider.GetService<ILiveNotifier>() is not { } notifier)
            {
                logger.LogWarning("The host offers no live notifier; a notification for tenant {Tenant} was dropped.", tenantId);
                return;
            }

            await send(notifier);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "A live notification for tenant {Tenant} could not be sent.", tenantId);
        }
    }
}
