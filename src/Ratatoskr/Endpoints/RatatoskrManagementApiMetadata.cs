namespace Ratatoskr.Endpoints;

/// <summary>
/// Marker metadata added to the management API endpoint group by
/// <see cref="ManagementApiEndpointExtensions.MapRatatoskrManagementApi"/>.
/// Used by <see cref="LocalRatatoskrBypassAuthorizationHandler"/> to scope
/// the in-process authorization bypass to only Ratatoskr management endpoints,
/// regardless of their URL path prefix.
/// </summary>
internal sealed class RatatoskrManagementApiMetadata { }
