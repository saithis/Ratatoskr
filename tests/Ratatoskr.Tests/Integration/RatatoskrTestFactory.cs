using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Ratatoskr.TestHost;

namespace Ratatoskr.Tests.Integration;

public class RatatoskrTestFactory : WebApplicationFactory<RatatoskrTestHostAppMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");

        // Management command consumers are singletons that reach into scoped DbContexts. A
        // captive dependency there is silent in production and only shows up as intermittent
        // corruption, so the test host refuses to build one.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
    }
}
