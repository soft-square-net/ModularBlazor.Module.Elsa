using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Contracts.Messaging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;

/// <summary>
/// Turns the events the module subscribes to into work for Elsa, as the event's tenant. It throws when that work fails,
/// so the host delivers the event again. Everything it does is safe to do again: the inbox row is in the tenant's
/// database and Elsa's changes are not part of that transaction.
/// </summary>
internal sealed class NewsroomEventHandler(ElsaEngine engine) : IIntegrationEventHandler
{
    public Task HandleAsync(IntegrationEnvelope envelope, CancellationToken ct) =>
        engine.RunInTenantAsync(envelope.TenantId, async (services, token) =>
        {
            var workflows = services.GetRequiredService<NewsroomWorkflows>();
            if (envelope.Type == NewsroomEventTypes.ProjectApproved)
            {
                await workflows.StartProjectAsync(ProjectApproved.Read(envelope.Data), token);
                return true;
            }

            if (envelope.Type == NewsroomEventTypes.ProjectStateChanged &&
                EventData.Text(envelope.Data, "state") is { } state && ProjectStates.Is(state, ProjectStates.Killed) &&
                EventData.Guid(envelope.Data, "projectId") is { } killed)
            {
                await workflows.CancelProjectAsync(killed, token);
                return true;
            }

            foreach (var subject in Subjects.Of(envelope))
                await workflows.SignalAsync(subject, token);
            return true;
        }, ct);
}
