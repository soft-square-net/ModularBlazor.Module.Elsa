using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Management.Materializers;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using soft.square.net.WebApi.Contracts.Messaging;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Events;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Platform;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure.Newsroom.Runtime;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(soft.square.net.WebApi.Modules.Elsa.IntegrationTests.PostgresServer))]

namespace soft.square.net.WebApi.Modules.Elsa.IntegrationTests;

/// <summary>One PostgreSQL container for the run; each test takes a database of its own.</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async Task<string> NewDatabaseAsync(CancellationToken ct)
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await create.ExecuteNonQueryAsync(ct);
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

internal sealed record SentMessage(string Tenant, string Target, string Type, JsonElement Payload);

internal sealed class RecordingMessenger : ILiveMessenger
{
    public ConcurrentQueue<SentMessage> Sent { get; } = new();

    public Task ToUserAsync(string tenantId, string userId, string type, JsonElement payload, CancellationToken ct)
    {
        Sent.Enqueue(new SentMessage(tenantId, userId, type, payload));
        return Task.CompletedTask;
    }

    public Task ToUnitAsync(string tenantId, string unitPath, string type, JsonElement payload, CancellationToken ct) =>
        ToUserAsync(tenantId, unitPath, type, payload, ct);
}

internal sealed class Tenants : ITenantDirectory
{
    public const string A = "tenant-a";
    public const string B = "tenant-b";
    public const string Suspended = "tenant-suspended";

    private readonly TenantInfo[] _all =
    [
        new(A, A, TenantStatus.Active, [], "server", "db"),
        new(B, B, TenantStatus.Active, [], "server", "db"),
        new(Suspended, Suspended, TenantStatus.Suspended, [], "server", "db")
    ];

    public IReadOnlyCollection<TenantInfo> All => _all;
    public TenantInfo? Find(string id) => _all.FirstOrDefault(t => t.Id == id);
    public TenantInfo? FindByHost(string host) => null;
    public Task NotifyChangedAsync(string tenantId, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class TestEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "tests";
    public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>One replica: the engine as the module makes it, started, over a given database.</summary>
internal sealed class Replica : IAsyncDisposable
{
    private readonly ServiceProvider _host;
    private bool _disposed;

    private Replica(ServiceProvider host, ElsaEngine engine)
    {
        _host = host;
        Engine = engine;
    }

    public ElsaEngine Engine { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Replica> StartAsync(
        string database, FakePlatform platform, RecordingMessenger messenger, Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:elsa"] = database,
            ["Elsa:IdentityAuthority"] = "http://identity.test",
            ["Elsa:PlatformApiBaseUrl"] = "http://api.test",
            ["Identity:ElsaClient:ClientSecret"] = "test",
            [SettingWorkflowBindingsKey] = "project-workflow",
            // Long, so that what a test sees happen came from a signal or from the activity's own timer.
            ["Elsa:Newsroom:RecheckInterval"] = "00:10:00",
            ["Elsa:Newsroom:SettleDelay"] = "00:00:01",
            ["Elsa:Newsroom:RetryDelay"] = "00:00:01",
            ["Elsa:Newsroom:TimerSweepGrace"] = "00:00:00"
        };
        foreach (var (key, value) in overrides ?? []) settings[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var host = new ServiceCollection();
        host.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
        host.AddSingleton<ITenantDirectory, Tenants>();
        host.AddSingleton<IWebHostEnvironment, TestEnvironment>();
        host.AddSingleton<IHostEnvironment>(provider => provider.GetRequiredService<IWebHostEnvironment>());
        host.AddSingleton(new DiagnosticListener("tests"));
        host.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var provider = host.BuildServiceProvider();

        var engine = new ElsaEngine(provider, configuration, configureServices: services =>
        {
            services.AddHttpClient(PlatformApiClient).ConfigurePrimaryHttpMessageHandler(() => platform);
            services.AddSingleton<ILiveMessenger>(messenger);
        });
        await engine.StartAsync(Ct);
        await engine.RefreshTenantsAsync(Ct);
        return new Replica(provider, engine);
    }

    private const string SettingWorkflowBindingsKey = "Elsa:Newsroom:ProjectWorkflow";
    private const string PlatformApiClient = "newsroom-platform";

    public Task<T> InTenantAsync<T>(string tenant, Func<IServiceProvider, CancellationToken, Task<T>> work) =>
        Engine.RunInTenantAsync(tenant, work, Ct);

    public Task PublishAsync(string tenant, IActivity root, string definitionId = "project-workflow") =>
        InTenantAsync(tenant, async (services, ct) =>
        {
            var publisher = services.GetRequiredService<IWorkflowDefinitionPublisher>();
            var definition = await publisher.NewAsync(cancellationToken: ct);
            definition.DefinitionId = definitionId;
            definition.Name = definitionId;
            definition.MaterializerName = JsonWorkflowMaterializer.MaterializerName;
            definition.StringData = services.GetRequiredService<IActivitySerializer>().Serialize(root);
            var result = await publisher.PublishAsync(definition, ct);
            result.Succeeded.ShouldBeTrue(string.Join("; ", result.ValidationErrors.Select(e => e.Message)));
            return true;
        });

    /// <summary>Delivers an event the way the host's inbox does: to the module's handler.</summary>
    public Task DeliverAsync(string tenant, string type, string subject, object data) =>
        new NewsroomEventHandler(Engine).HandleAsync(new IntegrationEnvelope(Guid.NewGuid(), type, tenant, subject,
            DateTimeOffset.UtcNow, "test", "test", JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web)), Ct);

    public Task ApproveAsync(string tenant, FakeProject project) =>
        DeliverAsync(tenant, "newsroom.project-approved.v1", $"project:{project.Id:D}", new
        {
            projectId = project.Id, proposalId = 1, version = 1, approvalNumber = project.ApprovalNumber,
            postId = project.PostId, postPublicId = "p1", postKind = "post.news", sectionId = project.SectionId,
            sectionPath = "/org/desk", categories = new[] { "news" }, title = "A story", authorId = "author",
            proposalUnitId = project.SectionId, proposalUnitPath = "/org/desk", approvedBy = "moderator",
            approvedAt = DateTimeOffset.UtcNow
        });

    public Task<IReadOnlyList<WorkflowInstance>> InstancesAsync(string tenant) =>
        InTenantAsync<IReadOnlyList<WorkflowInstance>>(tenant, async (services, ct) =>
            (await services.GetRequiredService<IWorkflowInstanceStore>().FindManyAsync(new WorkflowInstanceFilter(), ct)).ToList());

    public Task<IReadOnlyList<(string Id, string Name)>> BookmarksAsync(string tenant) =>
        InTenantAsync<IReadOnlyList<(string, string)>>(tenant, async (services, ct) =>
            (await services.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter(), ct))
            .Select(b => (b.Id, b.Name!)).ToList());

    public Task<int> SweepAsync(string tenant) =>
        InTenantAsync(tenant, (services, ct) => services.GetRequiredService<OverdueTimers>().ResumeAsync(ct));

    public async Task<WorkflowInstance> WaitForAsync(string tenant, WorkflowSubStatus status, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var instances = await InstancesAsync(tenant);
            if (instances is [var instance] && instance.SubStatus == status) return instance;
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"No single instance became {status}: " +
                    string.Join(", ", instances.Select(i => $"{i.SubStatus} incidents={string.Join("|", i.WorkflowState.Incidents.Select(x => x.Message))}")));
            await Task.Delay(200, Ct);
        }
    }

    public static async Task UntilAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Waited in vain for: {what}.");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>Stops the replica; its in-memory timers go with it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Engine.StopAsync(CancellationToken.None);
        await Engine.DisposeAsync();
        await _host.DisposeAsync();
    }
}
