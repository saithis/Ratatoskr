using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Ratatoskr.Management;
using Ratatoskr.UI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
var app = builder.Build();

// IMPORTANT: UseRatatoskrUiIfConfigured must run BEFORE UseRouting/UseAuthentication so
// the local backend dispatcher captures the full downstream pipeline (routing, auth, endpoints).
app.UseRatatoskrUiIfConfigured();

// Explicit UseRouting here so the captured pipeline includes route matching.
// The LocalBackendDispatcher clears IEndpointFeature on synthetic contexts to force
// re-routing; without UseRouting in the captured pipeline that re-routing cannot happen.
app.UseRouting();

// Wire the management API when the host registers the expected policy.
// MapRatatoskrManagementApi is itself a no-op when no transport registered
// configurators, so the only gate here is the authorization policy check.
var authOptions = app.Services.GetService<IOptions<AuthorizationOptions>>()?.Value;
if (authOptions?.GetPolicy("RatatoskrAdmin") is not null)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapRatatoskrManagementApi("RatatoskrAdmin");
}

// Map UI proxy routes when the UI is configured.
app.MapRatatoskrUiRoutesIfConfigured();

app.Run();
