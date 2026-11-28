using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using soft.square.net.WebApi.Contracts.Tenancy;
using soft.square.net.WebApi.Data;

namespace soft.square.net.WebApi.Modules.Elsa.Infrastructure.Data;

/// <summary>
/// The module's integration outbox and inbox, in each tenant's database. Nothing else: Elsa's own tables are in the
/// engine's database (<c>ConnectionStrings:elsa</c>), so the inbox cannot make what the handler does in Elsa happen
/// once. The handler is idempotent instead.
/// </summary>
public sealed class WorkflowEventsDbContext(DbContextOptions<WorkflowEventsDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    protected override string Schema => "elsa_events";

    protected override bool UsesIntegrationEvents => true;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
    }
}

internal sealed class WorkflowEventsDesignTimeFactory : IDesignTimeDbContextFactory<WorkflowEventsDbContext>
{
    public WorkflowEventsDbContext CreateDbContext(string[] args) =>
        new(TenantDbDesignTime.Options<WorkflowEventsDbContext>(args), TenantDbDesignTime.Tenant);
}
