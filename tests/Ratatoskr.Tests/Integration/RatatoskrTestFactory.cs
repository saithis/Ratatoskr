using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Ratatoskr.TestHost;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

public class RatatoskrTestFactory : WebApplicationFactory<RatatoskrTestHostAppMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
}
