using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Ratatoskr.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
var app = builder.Build();

// Map the Ratatoskr management API when tests configure it.
// The policy check prevents the startup-validation throw for tests that don't need management endpoints.
var configurators = app.Services.GetServices<IRatatoskrEndpointConfigurator>().ToList();
var authOptions = app.Services.GetService<IOptions<AuthorizationOptions>>()?.Value;
if (configurators.Count > 0 && authOptions?.GetPolicy("RatatoskrAdmin") is not null)
{
    app.UseAuthorization();
    app.MapRatatoskrManagementApi("RatatoskrAdmin");
}

app.Run();
