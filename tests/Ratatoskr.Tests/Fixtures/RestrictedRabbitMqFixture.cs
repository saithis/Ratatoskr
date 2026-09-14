using System.Globalization;
using Testcontainers.RabbitMq;
using TUnit.Core.Interfaces;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// A RabbitMQ container whose identities carry exactly the least-privilege permission
/// patterns Ratatoskr promises to work under:
/// <list type="bullet">
///   <item><description>configure: <c>{user}\..*</c></description></item>
///   <item><description>write: <c>{user}\..*|.*\.inbox$</c></description></item>
///   <item><description>read: <c>{user}\..*|.*(?&lt;!internal)$</c></description></item>
/// </list>
/// Every control-plane feature must work with nothing more than this, so the fixture
/// deliberately never hands out the administrator connection for anything but provisioning.
/// </summary>
/// <remarks>
/// The <see cref="AttackerIdentity"/> is an ordinary identity with the very same permissions.
/// It exists to prove the negative: the broker happily lets it publish into another service's
/// command exchange, so the agent — not the broker — has to reject the command.
/// </remarks>
public sealed class RestrictedRabbitMqFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>Identity used by the managed service in tests.</summary>
    public const string ServiceIdentity = "orders";

    /// <summary>A second managed service, used for multi-service and shared-prefix tests.</summary>
    public const string SecondServiceIdentity = "inventory";

    /// <summary>Identity used by the dashboard in tests.</summary>
    public const string DashboardIdentity = "dashboard";

    /// <summary>A third-party identity that must never be able to drive another service.</summary>
    public const string AttackerIdentity = "attacker";

    private static readonly string[] Identities =
    [
        ServiceIdentity,
        SecondServiceIdentity,
        DashboardIdentity,
        AttackerIdentity,
    ];

    private RabbitMqContainer? _container;

    /// <summary>Connection string for the administrator, for provisioning and assertions only.</summary>
    public string AdminConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Container not initialized");

    public async Task InitializeAsync()
    {
        _container = new RabbitMqBuilder("rabbitmq:4.3-alpine").Build();
        await _container.StartAsync();

        foreach (var identity in Identities)
        {
            await ProvisionAsync(identity);
        }
    }

    /// <summary>
    /// Returns an AMQP URI authenticating as <paramref name="identity"/>, which must be one of
    /// the identities this fixture provisions.
    /// </summary>
    public string ConnectionStringFor(string identity)
    {
        if (Array.IndexOf(Identities, identity) < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(identity),
                identity,
                $"Unknown identity. Provisioned identities: {string.Join(", ", Identities)}."
            );
        }

        var admin = new UriBuilder(AdminConnectionString)
        {
            UserName = identity,
            Password = identity,
        };
        return admin.Uri.ToString();
    }

    private async Task ProvisionAsync(string identity)
    {
        await ExecAsync("rabbitmqctl", "add_user", identity, identity);
        await ExecAsync(
            "rabbitmqctl",
            "set_permissions",
            "-p",
            "/",
            identity,
            Configure(identity),
            Write(identity),
            Read(identity)
        );
    }

    internal static string Configure(string identity) => $@"{identity}\..*";

    internal static string Write(string identity) => $@"{identity}\..*|.*\.inbox$";

    internal static string Read(string identity) => $@"{identity}\..*|.*(?<!internal)$";

    private async Task ExecAsync(params string[] command)
    {
        var container =
            _container ?? throw new InvalidOperationException("Container not initialized");
        var result = await container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' failed with exit code {1}.{2}{3}{2}{4}",
                    string.Join(' ', command),
                    result.ExitCode,
                    Environment.NewLine,
                    result.Stdout,
                    result.Stderr
                )
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
