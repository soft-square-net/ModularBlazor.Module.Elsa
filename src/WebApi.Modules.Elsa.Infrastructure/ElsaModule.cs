using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Contracts;
using soft.square.net.WebApi.Contracts.Messaging;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Data;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Data;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure;

/// <summary>Optional Elsa runtime module; install only in a dedicated workflow pool or enable in an all-in-one host.</summary>
public sealed class ElsaModule : IRuntimeModule, IModulePipeline
{
    public ModuleManifest Manifest { get; } = new("elsa", "Elsa Workflows", "0.1.0", "elsa")
    {
        Description = "Optional Elsa workflow engine for newsroom automation.",
        HotUnload = false,
        Subscriptions = [.. NewsroomEventTypes.Subscriptions],
        DefaultSettings = new Dictionary<string, string?>
        {
            ["Elsa:Schema"] = "elsa",
            ["Elsa:RunMigrations"] = "true",
            ["Elsa:Newsroom:RecheckInterval"] = "00:05:00", // a waiting activity looks again at least this often
            ["Elsa:Newsroom:SettleDelay"] = "00:00:02",
            ["Elsa:Newsroom:RetryDelay"] = "00:00:30",
            ["Elsa:Newsroom:TimerSweepInterval"] = "00:01:00",
            ["Elsa:Newsroom:TimerSweepGrace"] = "00:00:30",
            ["Elsa:Newsroom:ContentRole"] = "writer",
            ["Elsa:Newsroom:ManagerDelay"] = "00:05:00",
            ["Elsa:Newsroom:ReviewerRole"] = "", // set only where the workflow owns assignment (Content)
            ["Elsa:Newsroom:ReviewerDelay"] = "00:05:00"
        },
        Permissions =
        [
            new("workflow.view", "View tenant workflows and executions."),
            new("workflow.design", "Design and publish tenant workflows."),
            new("workflow.run", "Start and manage tenant workflows.")
        ]
    };

    // Only what the host's module container can hold. Elsa itself lives in ElsaEngine's own container.
    public void ConfigureServices(IServiceCollection services, IConfiguration settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(provider => new ElsaEngine(provider, settings));
        services.AddSingleton<ITenantInitializer, ElsaTenantInitializer>();
        services.AddHostedService<ElsaEngineService>(); // first: the sweeper starts after the engine
        services.AddHostedService<TimerSweeperService>();

        // The events that start and resume workflows, with their inbox in each tenant's database.
        services.AddTenantDbContext<WorkflowEventsDbContext>();
        // After AddTenantDbContext: tenant initializers run in registration order, so the schema is migrated first.
        services.AddIntegrationEvents<WorkflowEventsDbContext>(options => options.Source = "elsa");
        foreach (var pattern in NewsroomEventTypes.Subscriptions)
            services.AddIntegrationHandler<NewsroomEventHandler>(pattern);
    }

    public void ConfigurePipeline(IApplicationBuilder app)
    {
        app.UseMiddleware<ElsaWorkflowPermissionMiddleware>();
        var engine = app.ApplicationServices.GetRequiredService<ElsaEngine>();
        app.Run(engine.InvokeAsync);
    }

    public void ConfigureEndpoint(IEndpointConventionBuilder endpoint) => endpoint.RequireAuthorization();
}
