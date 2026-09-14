using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Ratatoskr.Management;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Http;
using Ratatoskr.UI;
using Ratatoskr.UI.Store;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
var app = builder.Build();

// Each test composes the services it needs, so the host maps only what is actually registered:
// the per-service API when there are operations to serve, and the dashboard when there is a
// store behind it. A test that wants separate policies per capability registers a
// ManagementApiPolicies; otherwise one policy guards everything.
var policies =
    app.Services.GetService<ManagementApiPolicies>()
    ?? ManagementApiPolicies.Single("RatatoskrAdmin");

var authorization = app.Services.GetService<IOptions<AuthorizationOptions>>()?.Value;
var policiesRegistered =
    authorization is not null
    && policies.Names.Distinct(StringComparer.Ordinal).All(name => authorization.GetPolicy(name) is not null);

if (policiesRegistered)
{
    app.UseAuthentication();
    app.UseAuthorization();

    if (app.Services.GetService<IManagementDispatcher>() is not null)
    {
        app.MapRatatoskrManagementApi(policies);
    }

    // The store context is scoped, and the host validates scopes, so ask inside one.
    await using var probe = app.Services.CreateAsyncScope();
    if (probe.ServiceProvider.GetService<RatatoskrDashboardDbContext>() is not null)
    {
        app.MapRatatoskrUI(policies, "/ratatoskr");
    }
}

await app.RunAsync();
