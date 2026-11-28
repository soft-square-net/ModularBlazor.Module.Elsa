using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using soft.square.net.WebApi.Modules.Elsa.Infrastructure;

namespace soft.square.net.WebApi.Modules.Elsa.UnitTests;

public class ElsaModuleRegistrations
{
    [Fact]
    public void The_module_registers_nothing_keyed_which_a_runtime_module_cannot_hold()
    {
        var services = new ServiceCollection();

        new ElsaModule().ConfigureServices(services, new ConfigurationBuilder().Build());

        services.ShouldNotBeEmpty();
        services.ShouldAllBe(service => !service.IsKeyedService);
    }
}
