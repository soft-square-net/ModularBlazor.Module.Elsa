using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.DryRun;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom;

internal static class NewsroomServices
{
    /// <summary>
    /// What the newsroom activities use, in the engine's container. The interfaces are the parts that later steps
    /// replace (bindings, routing rules, load) and the ones a test fakes.
    /// </summary>
    /// <param name="module">The module's container, which can open a tenant scope for the host's live notifier.</param>
    public static IServiceCollection AddNewsroom(this IServiceCollection services, IServiceProvider module)
    {
        services.AddHttpClient(PlatformApi.ClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PlatformServiceToken>();
        services.TryAddScoped<PlatformApi>();
        services.TryAddScoped<ContentModuleApi>();
        services.TryAddScoped<ShiftsModuleApi>();
        services.TryAddScoped<OrganizationModuleApi>();
        services.TryAddScoped<INewsRoomProjects, HttpNewsRoomProjects>();
        services.TryAddScoped<IWorkflowBindings, HttpWorkflowBindings>();
        services.TryAddScoped<IUnitRouter, HttpUnitRouter>();
        services.TryAddScoped<IShiftWorkload, HttpShiftWorkload>();
        services.TryAddSingleton<ILiveMessenger>(engine => new HostLiveMessenger(
            module, engine.GetRequiredService<ILoggerFactory>().CreateLogger<HostLiveMessenger>()));
        services.TryAddScoped<NewsroomWorkflows>();
        services.TryAddScoped<IContentWorkflows>(provider => provider.GetRequiredService<NewsroomWorkflows>());
        services.TryAddScoped<OverdueTimers>();
        services.TryAddScoped<DryRunner>();
        return services;
    }
}
