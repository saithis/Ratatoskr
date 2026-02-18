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
                        .WithOperation(o => o.WithDescription("Publishes API key lifecycle events.")))
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
                            .WithRole(EventCatalogRole.Client)))),
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
    public async Task Generate_MultipleMessagesOnChannel_DefaultPerMessageOperations()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("events", c => c
                    .Produces<ApiKeyRevokedEvent>()
                    .Produces<OrderCreatedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { HostName = "localhost" });

        var document = generator.Generate();

        // Channel still has both messages
        await Assert.That(document.Channels["events"].Messages!.Count).IsEqualTo(2);
        await Assert.That(document.Components!.Messages).ContainsKey("api-key.revoked");
        await Assert.That(document.Components!.Messages).ContainsKey("order.created");

        // But now there are two separate operations (one per message)
        await Assert.That(document.Operations.Count).IsEqualTo(2);
        await Assert.That(document.Operations).ContainsKey("sendApiKeyRevokedEvent");
        await Assert.That(document.Operations).ContainsKey("sendOrderCreatedEvent");
        await Assert.That(document.Operations["sendApiKeyRevokedEvent"].Messages!.Count).IsEqualTo(1);
        await Assert.That(document.Operations["sendOrderCreatedEvent"].Messages!.Count).IsEqualTo(1);
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
    public async Task Generate_ChannelLevelOperation_GroupsAllMessages()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("order.events", c => c
                    .WithAsyncApi(a => a.WithOperation(o => o.WithTitle("Publish Order Events")))
                    .Produces<OrderCreatedEvent>()
                    .Produces<ApiKeyRevokedEvent>()));

        var document = generator.Generate();

        // Single grouped operation using channel name as key
        await Assert.That(document.Operations.Count).IsEqualTo(1);
        await Assert.That(document.Operations).ContainsKey("order.events");
        await Assert.That(document.Operations["order.events"].Title).IsEqualTo("Publish Order Events");
        await Assert.That(document.Operations["order.events"].Messages!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Generate_ChannelLevelOperationId_CanBeOverridden()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithAsyncApi(a => a.WithOperation(o => o.WithId("partner-api-key-revoke")))
                    .Consumes<UserRolesChangedEvent>()));

        var document = generator.Generate();

        await Assert.That(document.Operations).ContainsKey("partner-api-key-revoke");
        await Assert.That(document.Operations.ContainsKey("user.events")).IsFalse();
    }

    [Test]
    public async Task Generate_SharedOperationId_MergesMessagesIntoOneOperation()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("order.events", c => c
                    .Consumes<OrderCreatedEvent>(m => m
                        .WithAsyncApi(a => a.WithOperation(o => o
                            .WithId("consumeOrderLifecycle")
                            .WithTitle("Consume Order Lifecycle"))))
                    .Consumes<ApiKeyRevokedEvent>(m => m
                        .WithAsyncApi(a => a.WithOperation(o => o
                            .WithId("consumeOrderLifecycle"))))
                    .Consumes<UserRolesChangedEvent>()));

        var document = generator.Generate();

        // Two messages with same operationId merged into one operation
        await Assert.That(document.Operations).ContainsKey("consumeOrderLifecycle");
        await Assert.That(document.Operations["consumeOrderLifecycle"].Messages!.Count).IsEqualTo(2);
        await Assert.That(document.Operations["consumeOrderLifecycle"].Title).IsEqualTo("Consume Order Lifecycle");

        // Third message gets its own operation
        await Assert.That(document.Operations).ContainsKey("receiveUserRolesChangedEvent");
        await Assert.That(document.Operations["receiveUserRolesChangedEvent"].Messages!.Count).IsEqualTo(1);

        await Assert.That(document.Operations.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Generate_PerMessageOperationCustomization()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("order.events", c => c
                    .Produces<OrderCreatedEvent>(m => m
                        .WithAsyncApi(a => a.WithOperation(o => o
                            .WithDescription("Emitted when a new order is placed."))))));

        var document = generator.Generate();

        await Assert.That(document.Operations).ContainsKey("sendOrderCreatedEvent");
        await Assert.That(document.Operations["sendOrderCreatedEvent"].Description)
            .IsEqualTo("Emitted when a new order is placed.");
    }

    [Test]
    public async Task Generate_DuplicateOperationId_AcrossChannels_Throws()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("channel1", c => c
                    .Produces<OrderCreatedEvent>())
                .AddEventPublishChannel("channel2", c => c
                    .Produces<OrderCreatedEvent>()));

        // Same message type on two channels → both default to "sendOrderCreatedEvent"
        Assert.Throws<InvalidOperationException>(() => generator.Generate());
    }

    [Test]
    public async Task Generate_OperationTags_IncludedInOutput()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("order.events", c => c
                    .Produces<OrderCreatedEvent>(m => m
                        .WithAsyncApi(a => a.WithOperation(o => o
                            .WithTags("orders", "lifecycle"))))));

        var document = generator.Generate();

        var op = document.Operations["sendOrderCreatedEvent"];
        await Assert.That(op.Tags).IsNotNull();
        await Assert.That(op.Tags!.Count).IsEqualTo(2);
        await Assert.That(op.Tags[0].Name).IsEqualTo("orders");
        await Assert.That(op.Tags[1].Name).IsEqualTo("lifecycle");
    }
}
