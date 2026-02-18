using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.AsyncApi.Config;
using Ratatoskr.AsyncApi.Extensions;
using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.CloudEvents;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.Tests.AsyncApi;

public class AsyncApiDocumentGeneratorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
    };

    private static AsyncApiDocumentGenerator BuildGenerator(
        Action<RatatoskrBuilder> busConfig,
        Action<AsyncApiOptions>? asyncApiConfig = null,
        RabbitMqOptions? rabbitMqOptions = null,
        CloudEventsContentMode contentMode = CloudEventsContentMode.Binary)
    {
        var services = new ServiceCollection();

        services.AddRatatoskr(bus =>
        {
            bus.ConfigureCloudEvents(ce => ce.ContentMode = contentMode);
            busConfig(bus);
        });

        services.AddAsyncApiDocumentation(opts =>
        {
            opts.WithTitle("Test Service").WithVersion("1.0.0");
            asyncApiConfig?.Invoke(opts);
        });

        if (rabbitMqOptions != null)
        {
            services.AddSingleton(rabbitMqOptions);
            services.AddRabbitMqAsyncApiBindings();
        }

        return services.BuildServiceProvider().GetRequiredService<AsyncApiDocumentGenerator>();
    }

    [Test]
    public async Task Generate_BinaryMode_PublishAndConsumeChannels_WithRabbitMqBindings()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("apikey.events", c => c
                    .WithAsyncApi(a => a
                        .WithDescription("Channel for API key related events.")
                        .WithOperationDescription("Publishes API key lifecycle events.")) // TODO: put operation on the Produces?
                    .WithRabbitMq(r => r.ExchangeTypeTopic())
                    .Produces<ApiKeyRevokedEvent>())
                .AddEventConsumeChannel("user.events", c => c
                    .WithAsyncApi(a => a
                        .WithDescription("Channel for authorization events owned by the user service."))
                    .WithRabbitMq(r => r
                        .ExchangeType("fanout")
                        .QueueName("apikey.subscriptions"))
                    .Consumes<UserRolesChangedEvent>(m => m
                        .WithAsyncApi(a => a
                            .WithVersion("2.0.0") 
                            .WithRole(EventCatalogRole.Client)))), // TODO: infer the EventCatalogRole and EventCatalogMessageType
            asyncApiConfig: opts => opts
                .WithDescription("AsyncAPI documentation for the API Key service."),
            rabbitMqOptions: new RabbitMqOptions { HostName = "rabbitmq.example.com" });

        var document = generator.Generate();
        var json = JsonSerializer.Serialize(document, JsonOptions);

        await Verify(json, extension: "json").UseDirectory("Snapshots");
    }

    [Test]
    public async Task Generate_StructuredMode_ProducesCloudEventEnvelope()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("orders.events", c => c
                    .Produces<OrderCreatedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { HostName = "localhost" },
            contentMode: CloudEventsContentMode.Structured);

        var document = generator.Generate();
        var json = JsonSerializer.Serialize(document, JsonOptions);

        await Verify(json, extension: "json").UseDirectory("Snapshots");
    }

    [Test]
    public async Task Generate_WithDataAnnotations_SchemaIncludesConstraints()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("orders.events", c => c
                    .Produces<OrderCreatedEvent>()));

        var document = generator.Generate();

        var orderSchema = document.Components!.Schemas!["OrderCreatedEvent"];

        await Assert.That(orderSchema).IsNotNull();
        await Assert.That(orderSchema.Properties!["amount"].Minimum).IsEqualTo(0.01);
        await Assert.That(orderSchema.Properties["amount"].Maximum).IsEqualTo(999999.99);
        await Assert.That(orderSchema.Properties["customerEmail"].Format).IsEqualTo("email");
        await Assert.That(orderSchema.Properties["callbackUrl"].Format).IsEqualTo("uri");
        await Assert.That(orderSchema.Properties["notes"].MaxLength).IsEqualTo(500);
        await Assert.That(orderSchema.Properties["notes"].MinLength).IsEqualTo(1);
        await Assert.That(orderSchema.Required).Contains("orderId");
        await Assert.That(orderSchema.Required).Contains("amount");
    }

    [Test]
    public async Task Generate_WithAsyncApiMessageAttribute_UsesAttributeMetadata()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("apikey.events", c => c
                    .Produces<ApiKeyRevokedEvent>()));

        var document = generator.Generate();

        var message = document.Components!.Messages!["api-key.revoked"];

        await Assert.That(message.Title).IsEqualTo("API Key Revoked");
        await Assert.That(message.Description).IsEqualTo("An API key has been revoked.");
        await Assert.That(message.Extensions!["x-eventcatalog-message-version"].GetString()).IsEqualTo("1.0.0");
        await Assert.That(message.Extensions["x-eventcatalog-message-type"].GetString()).IsEqualTo("event");
        await Assert.That(message.Extensions["x-eventcatalog-role"].GetString()).IsEqualTo("provider");
    }

    [Test]
    public async Task Generate_ConsumerChannel_RoleIsClient()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithRabbitMq(r => r.QueueName("my.queue"))
                    .Consumes<UserRolesChangedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { HostName = "localhost" });

        var document = generator.Generate();

        var message = document.Components!.Messages!["user-roles-changed"];
        await Assert.That(message.Extensions!["x-eventcatalog-role"].GetString()).IsEqualTo("client");
    }

    [Test]
    public async Task Generate_WithoutRabbitMq_ProducesTransportAgnosticDocument()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("events", c => c
                    .Produces<OrderCreatedEvent>()));

        var document = generator.Generate();
        var json = JsonSerializer.Serialize(document, JsonOptions);

        await Assert.That(document.Servers).IsNull();
        await Verify(json, extension: "json").UseDirectory("Snapshots");
    }

    [Test]
    public async Task Generate_MultipleMessagesOnChannel_AllInDocument()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("events", c => c
                    .Produces<ApiKeyRevokedEvent>()
                    .Produces<OrderCreatedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { HostName = "localhost" });

        var document = generator.Generate();

        await Assert.That(document.Channels["events"].Messages!.Count).IsEqualTo(2);
        await Assert.That(document.Components!.Messages).ContainsKey("api-key.revoked");
        await Assert.That(document.Components!.Messages).ContainsKey("order.created");
        await Assert.That(document.Operations["events"].Messages!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Generate_RabbitMqQueueChannel_IsAddedForConsumeChannels()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithRabbitMq(r => r
                        .ExchangeType("fanout")
                        .QueueName("apikey.subscriptions"))
                    .Consumes<UserRolesChangedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { HostName = "localhost" });

        var document = generator.Generate();

        await Assert.That(document.Channels).ContainsKey("user.events");
        await Assert.That(document.Channels).ContainsKey("apikey.subscriptions");

        var queueChannel = document.Channels["apikey.subscriptions"];
        await Assert.That(queueChannel.Bindings!.Amqp!.Is).IsEqualTo("queue");
        await Assert.That(queueChannel.Bindings.Amqp.Queue!.Name).IsEqualTo("apikey.subscriptions");
    }

    [Test]
    public async Task Generate_ChannelOperationId_CanBeOverridden()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithAsyncApi(a => a.WithOperationId("partner-api-key-revoke"))
                    .Consumes<UserRolesChangedEvent>()));

        var document = generator.Generate();

        await Assert.That(document.Operations).ContainsKey("partner-api-key-revoke");
        await Assert.That(document.Operations.ContainsKey("user.events")).IsFalse();
    }
}
