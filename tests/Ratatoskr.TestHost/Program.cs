using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Ratatoskr.Management;
using Ratatoskr.UI;
using Ratatoskr.UI.Client;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
var app = builder.Build();

// Wire the management API when the host registers the expected policy.
// MapRatatoskrManagementApi is itself a no-op when no transport registered
// configurators, so the only gate here is the authorization policy check.
var authOptions = app.Services.GetService<IOptions<AuthorizationOptions>>()?.Value;
if (authOptions?.GetPolicy("RatatoskrAdmin") is not null)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapRatatoskrManagementApi("RatatoskrAdmin");
    if (app.Services.GetService<IRatatoskrBrokerManagementClient>() is not null)
    {
        app.MapRatatoskrUI("RatatoskrAdmin", "/ratatoskr");
    }
}

await app.RunAsync();
