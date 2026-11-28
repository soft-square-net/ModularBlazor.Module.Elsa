// Elsa playground: a small platform with this repo's module in it, for local development and for the workflow check
// (playground/check.ps1). The Elsa module runs on its own "workflow" pool, as in a real deployment; the modules the
// newsroom workflows call run on the "core" pool. The WebApis load every module straight from its build output
// (Modules:Local, Development only): change code, rebuild, restart the pool in the dashboard.
//
//   identity (5090)  registers the service client "elsa-workflows"
//
//   postgres ─► core (5081)      Organization (central + its own connector), Shifts, DynamicFields, Content
//            └► workflow (5082)  Elsa                            ◄── gateway (5080) ◄── blzwebclient (5070)
//   broker (RabbitMQ): integration events and cross-pool cache invalidation      /api/elsa/** → workflow, else core
//   seed-grants: gives the dev tenants acme and globex their grants (seed-grants.ps1), then exits
//
// It uses the platform lab's ports (the client and identity are registered for them), so stop the lab, and any other
// module's playground, first. Data stays in the Docker volume "elsa-playground-data" and in playground/.data (the
// module catalogs and keys).

using System.Reflection;
using Aspire.Hosting.Yarp;
using Aspire.Hosting.Yarp.Transforms;

var builder = DistributedApplication.CreateBuilder(args);

// Fixed by the platform: the identity service registers the client's callbacks on 5070 and the client calls the
// gateway on 5080. The pools get fixed ports too, so a script can reach one directly.
const int IdentityPort = 5090;
const int GatewayPort = 5080;
const int CorePort = 5081;
const int WorkflowPort = 5082;
const int ClientPort = 5070;
const string Issuer = "http://localhost:5090/";
const string ClientOrigin = "http://localhost:5070";
const string GatewayUrl = "http://localhost:5080";

// The service client the Elsa module signs in with (client credentials, scope "api"). Its secret comes from the app
// host's user-secrets (dotnet user-secrets set Parameters:elsa-client-secret <value> --project playground); without
// one it is the development value of the platform's identity service. DEVELOPMENT ONLY: never a real secret here.
// The identity service registers one configurable service client, so the playground's Organization connector and the
// Shifts module sign in with it too. A real deployment gives each its own client.
const string ElsaClientId = "elsa-workflows";
var elsaClientSecret = string.IsNullOrEmpty(builder.Configuration["Parameters:elsa-client-secret"])
    ? builder.AddParameter("elsa-client-secret", "dev-elsa-workflows-secret", secret: true)
    : builder.AddParameter("elsa-client-secret", secret: true);

// ---------------------------------------------------------------- infrastructure

var tenantData = builder.AddPostgres("tenant-data")
    .WithDataVolume("elsa-playground-data")
    .WithPgAdmin();
var tenantStore = tenantData.AddDatabase("TenantStore", databaseName: "PlaygroundTenants");
var identityDb = tenantData.AddDatabase("identity-db", databaseName: "identity");
// Elsa keeps every tenant's workflows in this one database (schema "elsa"), separated by Elsa's tenant id.
var elsaDb = tenantData.AddDatabase("elsa-db", databaseName: "ElsaWorkflows");

var broker = builder.AddRabbitMQ("broker").WithManagementPlugin();

// ---------------------------------------------------------------- identity

// Development settings of the identity service: dev accounts (admin@modular.local, acme-admin@modular.local, ...).
// The playground adds an administrator of the second tenant, so each tenant has one of its own.
var identity = builder.AddProject<Projects.WebApi_Identity_Server>("identity", launchProfileName: null)
    .WithHttpEndpoint(port: IdentityPort)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Identity__Issuer", Issuer)
    .WithEnvironment("Identity__ElsaClient__ClientId", ElsaClientId)
    .WithEnvironment("Identity__ElsaClient__ClientSecret", elsaClientSecret)
    .WithEnvironment("Identity__Seed__Users__3__Email", "globex-admin@modular.local")
    .WithEnvironment("Identity__Seed__Users__3__Password", "Dev-GlobexAdmin-2026!")
    .WithEnvironment("Identity__Seed__Users__3__DisplayName", "Globex Admin")
    .WithEnvironment("Identity__Seed__Users__3__Memberships__0__Tenant", "globex")
    .WithEnvironment("Identity__Seed__Users__3__Memberships__0__Role", "tenant-admin")
    .WithReference(identityDb, connectionName: "identity").WaitFor(identityDb)
    .WithHttpHealthCheck("/health");

// ---------------------------------------------------------------- the pools

var data = Path.Combine(Metadata("PlaygroundRoot"), ".data");
var siblingsRoot = builder.Configuration["Playground:SiblingsRoot"] ?? Path.Combine(Metadata("RepoRoot"), "..");

var core = AddPool("core", CorePort)
    .WithEnvironment("Modules__Excluded__0", "elsa");

// The modules the workflows work with, each loaded from a built checkout (Debug build) next to this repo.
// NewsRoom is optional until its projects are merged: without it the playground runs, and the newsroom workflows that
// need a project wait.
var siblings = new (string Name, string Id, bool Optional, string[] ClientParts)[]
{
    ("Organization", "organization", false, ["WebApi.Modules.Organization.Client", "Module.Organization"]),
    ("Shifts", "shifts", false, ["WebApi.Modules.Shifts.Client", "Module.Shifts"]),
    ("DynamicFields", "dynamicfields", false, ["WebApi.Modules.DynamicFields.Client", "Module.DynamicFields"]),
    ("Content", "content", false, ["WebApi.Modules.Content.Client", "Module.Content"]),
    ("NewsRoom", "newsroom", true, ["WebApi.Modules.NewsRoom.Client", "Module.NewsRoom"]),
};
foreach (var (name, id, optional, clientParts) in siblings)
{
    var root = Path.GetFullPath(builder.Configuration[$"Playground:{name}"] ?? Path.Combine(siblingsRoot, $"Module.{name}"));
    var web = Path.Combine(root, "src", $"WebApi.Modules.{name}.Web", "bin", "Debug", "net10.0");
    var parts = clientParts.Select(p => (Part: p, Dll: Path.Combine(root, "src", p, "bin", "Debug", "net10.0", $"soft.square.net.{p}.dll"))).ToArray();
    var missing = parts.Select(p => p.Dll).Append(Path.Combine(web, $"soft.square.net.WebApi.Modules.{name}.Web.dll"))
        .Where(file => !File.Exists(file)).ToArray();
    if (missing.Length > 0 && optional)
    {
        Console.WriteLine($"{name}: not loaded, there is no Debug build in {root}.");
        continue;
    }
    // The workflows need all of them: stop here instead of starting a platform that half works.
    if (missing.Length > 0)
        throw new InvalidOperationException(
            $"{name}: no Debug build in {root} (missing {missing[0]}). Check out ModularBlazor.Module.{name} there and run dotnet build, " +
            $"or set Playground:SiblingsRoot or Playground:{name}.");

    Console.WriteLine($"{name}: loading the module built in {root}.");
    core.WithEnvironment($"Modules__Local__{id}", web + Path.DirectorySeparatorChar);
    foreach (var (part, dll) in parts)
        core.WithEnvironment($"Modules__Settings__client-modules__ClientModules__Local__{part}", dll);
}

// Organization: this one deployment is the central service, and its connector reads from itself. Shifts reads the
// unit tree from the same deployment, through the gateway.
const string Org = "Modules__Settings__organization__Organization__";
const string ShiftsOrg = "Modules__Settings__shifts__Shifts__Organization__";
core.WithEnvironment("Platform__DeploymentId", "elsa-playground")
    .WithEnvironment("Tenancy__Seeds__Interval", "00:00:15")
    .WithEnvironment(Org + "Mode", "Central")
    .WithEnvironment(Org + "TrustedIssuers", Issuer)
    .WithEnvironment(Org + "ServiceClients", ElsaClientId)
    .WithEnvironment(Org + "CentralUrl", GatewayUrl + "/api/organization/")
    .WithEnvironment(Org + "TokenEndpoint", Issuer + "connect/token")
    .WithEnvironment(Org + "ClientId", ElsaClientId)
    .WithEnvironment(Org + "ClientScope", "api")
    .WithEnvironment(Org + "ClientSecret", elsaClientSecret)
    .WithEnvironment(Org + "PollInterval", "00:00:10")
    .WithEnvironment(ShiftsOrg + "BaseUrl", GatewayUrl + "/")
    .WithEnvironment(ShiftsOrg + "TokenEndpoint", Issuer + "connect/token")
    .WithEnvironment(ShiftsOrg + "ClientId", ElsaClientId)
    .WithEnvironment(ShiftsOrg + "ClientScope", "api")
    .WithEnvironment(ShiftsOrg + "ClientSecret", elsaClientSecret)
    .WithEnvironment("Modules__Settings__content__Content__ApiBaseUrl", GatewayUrl)
    .WithEnvironment("Modules__Settings__newsroom__NewsRoom__ApiBaseUrl", GatewayUrl);

// The workflow pool runs Elsa and nothing else (beside the platform's core modules). Only this pool gets the Elsa
// database and the client secret; the host passes both to the Elsa module alone.
var workflow = AddPool("workflow", WorkflowPort)
    .WithEnvironment("Modules__Enabled__0", "elsa")
    .WithEnvironment("Modules__Local__elsa", Metadata("ModuleOutput"))
    .WithReference(elsaDb, connectionName: "elsa").WaitFor(elsaDb)
    .WithEnvironment("Identity__ElsaClient__ClientId", ElsaClientId)
    .WithEnvironment("Identity__ElsaClient__ClientSecret", elsaClientSecret)
    .WithEnvironment("Elsa__IdentityAuthority", Issuer)
    .WithEnvironment("Elsa__PlatformApiBaseUrl", GatewayUrl)
    .WithEnvironment("Modules__Settings__elsa__Elsa__PollInterval", "00:00:10")
    // The role that produces contents (check-newsroom.ps1 puts its writer on shift with it), and a short delay for
    // a manager to assign by hand first.
    .WithEnvironment("Modules__Settings__elsa__Elsa__Newsroom__ContentRole", "lead-editor")
    .WithEnvironment("Modules__Settings__elsa__Elsa__Newsroom__ManagerDelay", "00:00:02")
    // One pool at a time creates the platform's shared tables and the dev tenants.
    .WaitFor(core);

// ---------------------------------------------------------------- edge

var gateway = builder.AddYarp("gateway")
    .WithHostPort(GatewayPort)
    .WithConfiguration(yarp =>
    {
        var coreCluster = yarp.AddCluster("core", [core.GetEndpoint("http")]);
        var workflowCluster = yarp.AddCluster("workflow", [workflow.GetEndpoint("http")]);
        yarp.AddRoute("/api/elsa/{**catch-all}", workflowCluster);
        yarp.AddRoute("/{**catch-all}", coreCluster);
        // Lab only, as in the platform's Hybird lab: reach one pool directly (/pools/workflow/scalar, ...).
        foreach (var (pool, cluster) in new[] { ("core", coreCluster), ("workflow", workflowCluster) })
            yarp.AddRoute($"/pools/{pool}/{{**catch-all}}", cluster)
                .WithTransformPathRemovePrefix($"/pools/{pool}")
                .WithTransformRequestHeader("X-Forwarded-Prefix", $"/pools/{pool}", append: false);
    })
    .WithReference(core).WaitFor(core)
    .WithReference(workflow).WaitFor(workflow);

builder.AddProject<Projects.ModularBlazor_BlzWebClient>("blzwebclient", launchProfileName: null)
    .WithHttpEndpoint(port: ClientPort)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WaitFor(identity)
    .WaitFor(gateway);

// The grants a tenant needs before the workflows can work in it: for the service subject elsa-workflows, and for the
// two tenant administrators. Idempotent; it exits when done (see its log in the dashboard).
var windows = OperatingSystem.IsWindows();
string[] seedArguments = ["-NoProfile", .. (windows ? ["-ExecutionPolicy", "Bypass"] : Array.Empty<string>()), "-File", "seed-grants.ps1"];
builder.AddExecutable("seed-grants", windows ? "powershell" : "pwsh", Metadata("PlaygroundRoot"), seedArguments)
    .WithEnvironment("PLAYGROUND_PLATFORM_ROOT", Metadata("PlatformRoot"))
    .WaitFor(gateway);

builder.Build().Run();

// One WebApi pool (a single replica): the same image, told apart by configuration. Each pool keeps its own module
// catalog in playground/.data/<pool>; the broker keeps their caches in step.
IResourceBuilder<ProjectResource> AddPool(string name, int port) =>
    builder.AddProject<Projects.WebApi>(name, launchProfileName: null)
        .WithHttpEndpoint(port: port)
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command", "Warning")
        .WithEnvironment("Modules__Pool", name)
        .WithEnvironment("Modules__Role", "Api")
        .WithEnvironment("Modules__Path", Path.Combine(data, name, "modules"))
        .WithEnvironment("Modules__Settings__client-modules__ClientModules__Path", Path.Combine(data, name, "client-modules"))
        .WithReference(broker).WaitFor(broker)
        .WithReference(tenantStore).WaitFor(tenantStore)
        .WithEnvironment("DatabaseServers__TenantStore__Provider", "PostgreSql")
        .WithReference(tenantData)
        .WithEnvironment("DatabaseServers__tenant-data__Provider", "PostgreSql")
        .WithEnvironment("Auth__Authority", Issuer)
        .WithEnvironment("Auth__Audience", "modular-webapi")
        .WithEnvironment("Cors__AllowedOrigins__0", ClientOrigin)
        .WithTenantAdminGrants()
        .WaitFor(identity)
        .WithHttpHealthCheck("/health");

// Paths the project file computes (AssemblyMetadata in Elsa.Playground.csproj).
static string Metadata(string key) =>
    Assembly.GetEntryAssembly()!.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

internal static class PlaygroundGrants
{
    // "module.*" means every permission the module declares. A pool only knows the modules in its own catalog, so
    // Elsa's permissions are named one by one.
    private static readonly string[] TenantAdmin =
    [
        "access.grants.manage", "workflow.view", "workflow.design", "workflow.run",
        "content.*", "shifts.*", "dynamicfields.*", "newsroom.*"
    ];

    /// <summary>
    /// Development only (Access:DevGrants): ordinary grants (source "dev"), written the first time each tenant's
    /// administrator calls a pool. The service subject has no email, so seed-grants.ps1 grants it through the API.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithTenantAdminGrants(this IResourceBuilder<ProjectResource> pool)
    {
        var index = 0;
        foreach (var tenant in new[] { "acme", "globex" })
        {
            pool.WithEnvironment($"Access__DevGrants__{index}__Tenant", tenant)
                .WithEnvironment($"Access__DevGrants__{index}__Email", $"{tenant}-admin@modular.local");
            for (var i = 0; i < TenantAdmin.Length; i++)
                pool.WithEnvironment($"Access__DevGrants__{index}__Permissions__{i}", TenantAdmin[i]);
            index++;
        }
        return pool;
    }
}
