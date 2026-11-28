using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Contracts;
using soft.square.net.WebApi.Contracts.Tenancy;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure;

/// <summary>Optional Elsa runtime module; install only in a dedicated workflow pool or enable in an all-in-one host.</summary>
public sealed class ElsaModule : IRuntimeModule, IModulePipeline
{
    public ModuleManifest Manifest { get; } = new("elsa", "Elsa Workflows", "0.1.0", "elsa")
    {
        Description = "Optional Elsa workflow engine for newsroom automation.",
        HotUnload = false,
        DefaultSettings = new Dictionary<string, string?>
        {
            ["Elsa:Schema"] = "elsa",
            ["Elsa:RunMigrations"] = "true",
            ["Elsa:PollInterval"] = "00:00:15",
            ["Elsa:HandoverLookback"] = "1.00:00:00" // days.hours: "24:00:00" would mean 24 days
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
        services.AddHostedService<ElsaEngineService>(); // first: the coordinator starts after the engine
        services.AddHttpClient<NewsroomAutomationClient>();
        services.AddHostedService<NewsroomAutomationService>();
    }

    public void ConfigurePipeline(IApplicationBuilder app)
    {
        app.UseMiddleware<ElsaWorkflowPermissionMiddleware>();
        var engine = app.ApplicationServices.GetRequiredService<ElsaEngine>();
        app.Run(engine.InvokeAsync);
    }

    public void ConfigureEndpoint(IEndpointConventionBuilder endpoint) => endpoint.RequireAuthorization();
}
