using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class RatatoskrTestFactory(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");

        builder.ConfigureTestServices(services =>
        {
            // Register infrastructure components
            // Any specific overrides for tests can go here or be configured per-scope
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Ensure we hook into the host building process if needed, 
        // but typically ConfigureWebHost is enough for TestServer.
        // We'll expose the connection strings through configuration or direct service replacement in tests.
        
        return base.CreateHost(builder);
    }
}
