using System.Diagnostics;
using System.Runtime.Loader;
using System.Security.Claims;
using Elsa;
using Elsa.Common.Multitenancy;
using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Features.Services;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Scheduling;
using Elsa.Scheduling.Services;
using Elsa.Tenants.Extensions;
using Elsa.Workflows.Runtime.Distributed.Extensions;
using Medallion.Threading.Postgres;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.DryRun;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using soft.square.net.WebApi.Contracts.Tenancy;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure;

/// <summary>
/// Elsa with its own service container. The platform's runtime modules share the host's services through a small
/// overlay container, which has no keyed services; Elsa registers some. So Elsa gets a standard container of its own,
/// and the host lends it only what <see cref="BorrowHostServices"/> lists.
/// <para>
/// The host decides the tenant and the caller's rights. A request enters Elsa through <see cref="InvokeAsync"/> only
/// after <see cref="ElsaWorkflowPermissionMiddleware"/> let it through, and runs as the tenant the host resolved: Elsa
/// filters every definition, instance and bookmark by it. Elsa resolves no tenant of its own.
/// </para>
/// </summary>
internal sealed class ElsaEngine : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly RequestDelegate _pipeline;
    private readonly List<IHostedService> _running = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _refresh = new(1, 1);

    /// <param name="configureElsa">Adds to Elsa's setup; for tests.</param>
    /// <param name="configureServices">Replaces services of the engine's container; for tests.</param>
    public ElsaEngine(IServiceProvider host, IConfiguration settings,
        Action<IModule>? configureElsa = null, Action<IServiceCollection>? configureServices = null)
    {
        using var context = InModuleLoadContext();
        var services = new ServiceCollection();
        BorrowHostServices(services, host);
        services.AddSingleton(settings); // allowlisted module configuration only; never the host configuration root
        AddElsa(services, settings, configureElsa);
        services.AddNewsroom(host);
        configureServices?.Invoke(services);
        _services = services.BuildServiceProvider();
        _pipeline = BuildPipeline(_services);
    }

    /// <summary>
    /// The engine's own container. Work outside a request goes through <see cref="RunInTenantAsync{T}"/>.
    /// </summary>
    internal IServiceProvider Services => _services;

    /// <summary>
    /// The only way to use Elsa outside a request: a scope of the engine's container, as one tenant, inside the module's
    /// load context. Without a tenant Elsa silently uses an empty tenant id, and no tenant sees what it stored, so an
    /// empty, unknown or inactive tenant is refused. Waits until the engine has started.
    /// </summary>
    public async Task<T> RunInTenantAsync<T>(string? tenantId,
        Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Elsa runs for a tenant only: no tenant was given.");
        if (_services.GetRequiredService<ITenantDirectory>().Find(tenantId) is not { IsActive: true } tenant)
            throw new InvalidOperationException($"Elsa runs for active tenants only: '{tenantId}' is unknown or inactive.");
        await _started.Task.WaitAsync(cancellationToken);

        using var loadContext = InModuleLoadContext();
        await using var scope = _services.CreateAsyncScope();
        using var asTenant = _services.GetRequiredService<ITenantAccessor>()
            .PushContext(new Tenant { Id = tenant.Id, Name = tenant.Name });
        return await work(scope.ServiceProvider, cancellationToken);
    }

    /// <summary>
    /// EF Core, a host assembly, loads Elsa's migrations by assembly name, which the host's load context cannot resolve:
    /// the module's assemblies are in the module's own context. This makes such loads look there.
    /// </summary>
    private static AssemblyLoadContext.ContextualReflectionScope InModuleLoadContext() =>
        AssemblyLoadContext.EnterContextualReflection(typeof(ElsaEngine).Assembly);

    private static void BorrowHostServices(IServiceCollection services, IServiceProvider host)
    {
        // Registered as instances: this container uses them and never disposes them.
        Borrow<ILoggerFactory>();
        Borrow<IHostEnvironment>();
        Borrow<IWebHostEnvironment>();
        Borrow<IHostApplicationLifetime>();
        Borrow<IHttpContextAccessor>();
        Borrow<DiagnosticListener>();
        Borrow<IDataProtectionProvider>(); // the host's keys, shared by every replica
        Borrow<ITenantDirectory>();
        if (host.GetService<DiagnosticListener>() is { } diagnostics) services.AddSingleton<DiagnosticSource>(diagnostics);
        services.AddLogging();

        void Borrow<T>() where T : class
        {
            if (host.GetService<T>() is { } instance) services.AddSingleton(instance);
        }
    }

    private static void AddElsa(IServiceCollection services, IConfiguration settings, Action<IModule>? configure)
    {
        var connection = settings.GetConnectionString("elsa")
            ?? throw new InvalidOperationException("The host must provide ConnectionStrings:elsa from a secret provider.");
        var databaseOptions = new ElsaDbContextOptions { SchemaName = settings["Elsa:Schema"] ?? "elsa" };
        var runMigrations = settings.GetValue("Elsa:RunMigrations", true);

        services.AddRouting();
        services.AddAuthorization();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef =>
            {
                ef.RunMigrations = runMigrations;
                ef.UsePostgreSql(connection, databaseOptions);
            }));
            elsa.UseWorkflowRuntime(runtime =>
            {
                runtime.UseEntityFrameworkCore(ef =>
                {
                    ef.RunMigrations = runMigrations;
                    ef.UsePostgreSql(connection, databaseOptions);
                });
                // Several replicas share the database: an instance runs in one of them at a time. The default lock
                // provider locks files, which reaches one machine only.
                runtime.UseDistributedRuntime();
                runtime.DistributedLockProvider = _ => new PostgresDistributedSynchronizationProvider(connection);
            });
            elsa.UseScheduling(); // delays and deadlines
            elsa.UseTenants();
            elsa.UseWorkflowsApi();
            elsa.UseHttp();
            elsa.AddActivitiesFrom<ElsaEngine>();
            DefaultWorkflows.Register(elsa);
            configure?.Invoke(elsa);
        });
        services.AddSingleton<LocalScheduler>();
        services.AddSingleton<StoppableScheduler>();
        services.AddSingleton<IScheduler>(provider => provider.GetRequiredService<StoppableScheduler>());
        // Elsa's background work runs once per tenant of the platform's single tenant directory.
        services.AddSingleton<ITenantsProvider, ElsaPlatformTenantsProvider>();
    }

    private static RequestDelegate BuildPipeline(IServiceProvider services)
    {
        var app = new ApplicationBuilder(services);
        app.UseRouting();
        app.UseAuthorization();
        app.UseWorkflows(); // HTTP workflow triggers, under /workflows
        // The API, under /elsa/api. Not UseWorkflowsApi: it needs a builder that is also the route builder.
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapWorkflowsApi();
            DryRunEndpoint.Map(endpoints);
        });
        return app.Build();
    }

    /// <summary>Starts Elsa's hosted services (migrations, tenant activation, background dispatchers), in order.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The background work these services start keeps the context.
        using var context = InModuleLoadContext();
        try
        {
            foreach (var service in _services.GetServices<IHostedService>())
            {
                var lifecycle = service as IHostedLifecycleService;
                if (lifecycle is not null) await lifecycle.StartingAsync(cancellationToken);
                await service.StartAsync(cancellationToken);
                _running.Add(service);
                if (lifecycle is not null) await lifecycle.StartedAsync(cancellationToken);
            }
            _started.TrySetResult();
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var i = _running.Count - 1; i >= 0; i--)
            await _running[i].StopAsync(cancellationToken);
        _running.Clear();
        await _services.GetRequiredService<StoppableScheduler>().StopAllAsync();
    }

    /// <summary>
    /// Makes Elsa pick up the tenant directory's current tenants (the startup tasks of a new one). The host calls it
    /// for every tenant before the module serves it.
    /// </summary>
    public async Task RefreshTenantsAsync(CancellationToken cancellationToken)
    {
        await _started.Task.WaitAsync(cancellationToken);
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            using var context = InModuleLoadContext();
            await _services.GetRequiredService<ITenantService>().RefreshAsync(cancellationToken);
        }
        finally { _refresh.Release(); }
    }

    /// <summary>Runs one request in Elsa's pipeline, as the host's tenant and with the host's permission decision.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!_started.Task.IsCompletedSuccessfully)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Without a tenant Elsa would read and write its default tenant's data, so there is no request without one.
        if (context.RequestServices.GetService<ITenantContext>()?.Tenant is not { } tenant)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var (hostServices, user) = (context.RequestServices, context.User);
        using var loadContext = InModuleLoadContext();
        await using var scope = _services.CreateAsyncScope();
        using var asTenant = _services.GetRequiredService<ITenantAccessor>()
            .PushContext(new Tenant { Id = tenant.Id, Name = tenant.Name });
        try
        {
            context.RequestServices = scope.ServiceProvider;
            context.User = GrantElsaApi(user);
            await _pipeline(context);
        }
        finally
        {
            context.RequestServices = hostServices;
            context.User = user;
        }
    }

    /// <summary>
    /// Elsa's API checks its own permission claims. The platform's permission was checked before, so inside Elsa the
    /// caller holds them all. The caller's own identities stay first, and the host never sees the added one.
    /// </summary>
    private static ClaimsPrincipal GrantElsaApi(ClaimsPrincipal user)
    {
        var granted = new ClaimsPrincipal(user.Identities);
        granted.AddIdentity(new ClaimsIdentity([new Claim(PermissionNames.ClaimType, PermissionNames.All)]));
        return granted;
    }

    public async ValueTask DisposeAsync()
    {
        _refresh.Dispose();
        await _services.DisposeAsync();
    }
}

/// <summary>Runs the engine as one of the module's hosted services.</summary>
internal sealed class ElsaEngineService(ElsaEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => engine.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => engine.StopAsync(cancellationToken);
}

/// <summary>The host runs this for every active tenant, and again for one added or re-activated later.</summary>
internal sealed class ElsaTenantInitializer(ElsaEngine engine) : ITenantInitializer
{
    public Task InitializeAsync(TenantInfo tenant, CancellationToken ct) => engine.RefreshTenantsAsync(ct);
}

/// <summary>Elsa's background services enumerate active tenants from the platform's single tenant directory.</summary>
internal sealed class ElsaPlatformTenantsProvider(ITenantDirectory directory) : ITenantsProvider
{
    public Task<IEnumerable<Tenant>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<Tenant>>(directory.All.Where(t => t.IsActive)
            .Select(t => new Tenant { Id = t.Id, Name = t.Name }).ToArray());

    public Task<Tenant?> FindAsync(TenantFilter filter, CancellationToken cancellationToken = default) =>
        Task.FromResult(directory.Find(filter.Id ?? string.Empty) is { IsActive: true } tenant
            ? new Tenant { Id = tenant.Id, Name = tenant.Name }
            : null);
}
