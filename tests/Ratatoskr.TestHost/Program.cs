using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Ratatoskr.Management;
using Ratatoskr.UI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
builder.Services.AddRatatoskrUI();

var app = builder.Build();

// Wire the management API and UI when the host registers the expected policy.
var authOptions = app.Services.GetService<IOptions<AuthorizationOptions>>()?.Value;
if (authOptions?.GetPolicy("RatatoskrAdmin") is not null)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapRatatoskrManagementApi("RatatoskrAdmin");
    app.MapRatatoskrUI("/ratatoskr");
}

await app.RunAsync();
