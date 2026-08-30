using AwesomeAssertions;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.UI;

/// <summary>
/// Configuration-time rules for multi-service mode. A service name is a route segment and the
/// key the dashboard addresses a service by, so what these guard against would otherwise only
/// show up as a mis-routed request at runtime.
/// </summary>
public class RatatoskrUIOptionsTests
{
    [Test]
    public void AddService_AppendsDefaultManagementApiPathToTheServiceRoot()
    {
        var options = new RatatoskrUIOptions();

        options.AddService("orders", "https://orders.internal");

        options.RemoteServices.Should().ContainSingle();
        options.RemoteServices[0].Name.Should().Be("orders");
        options
            .RemoteServices[0]
            .ManagementApiUrl.Should()
            .Be("https://orders.internal/ratatoskr/api/v1");
    }

    [Test]
    public void AddService_NormalizesRedundantSlashesBetweenRootAndPath()
    {
        var options = new RatatoskrUIOptions();

        options.AddService("orders", "https://orders.internal/edge/", "/admin/ratatoskr/");

        options
            .RemoteServices[0]
            .ManagementApiUrl.Should()
            .Be("https://orders.internal/edge/admin/ratatoskr");
    }

    [Test]
    public void AddService_WithUri_ResolvesTheSameManagementApiUrl()
    {
        var options = new RatatoskrUIOptions();

        options.AddService("orders", new Uri("https://orders.internal"));

        options
            .RemoteServices[0]
            .ManagementApiUrl.Should()
            .Be("https://orders.internal/ratatoskr/api/v1");
    }

    [Test]
    public void AddService_WithServiceDiscoveryScheme_KeepsTheSchemeIntact()
    {
        var options = new RatatoskrUIOptions();

        // Aspire hands out URLs such as "https+http://inventoryservice"; the HttpClient resolves
        // them, so the options must not reject or rewrite them.
        options.AddService("inventory", "https+http://inventoryservice");

        options
            .RemoteServices[0]
            .ManagementApiUrl.Should()
            .Be("https+http://inventoryservice/ratatoskr/api/v1");
    }

    [Test]
    public void AddService_WithDuplicateName_Throws()
    {
        var options = new RatatoskrUIOptions();
        options.AddService("orders", "https://orders-a.internal");

        // Two services under one name would make the second unreachable, because the dashboard
        // proxy resolves its target by name.
        var act = () => options.AddService("ORDERS", "https://orders-b.internal");

        act.Should().Throw<ArgumentException>().WithMessage("*already registered*");
    }

    [Test]
    public void AddService_WithSlashInName_Throws()
    {
        var options = new RatatoskrUIOptions();

        var act = () => options.AddService("orders/eu", "https://orders.internal");

        act.Should().Throw<ArgumentException>().WithMessage("*route segment*");
    }

    [Test]
    public void AddService_WithRelativeUrl_Throws()
    {
        var options = new RatatoskrUIOptions();

        // On Unix this parses as an absolute file: URI, so it has to be rejected on the host
        // rather than on Uri.IsAbsoluteUri alone.
        var act = () => options.AddService("orders", "/ratatoskr/api/v1");

        act.Should().Throw<ArgumentException>().WithMessage("*absolute base URL with a host*");
    }

    [Test]
    public void AddService_WithHostlessUri_Throws()
    {
        var options = new RatatoskrUIOptions();

        var act = () => options.AddService("orders", new Uri("file:///var/run/orders"));

        act.Should().Throw<ArgumentException>().WithMessage("*absolute base URL with a host*");
    }

    [Test]
    public void RemoteServices_IsEmptyByDefault()
    {
        var options = new RatatoskrUIOptions();

        options.RemoteServices.Should().BeEmpty();
        options.IncludeLocalService.Should().BeTrue();
    }
}
