using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ratatoskr.TestHost;

namespace Ratatoskr.Tests.Integration;

public class RatatoskrTestFactory : WebApplicationFactory<RatatoskrTestHostAppMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
}
