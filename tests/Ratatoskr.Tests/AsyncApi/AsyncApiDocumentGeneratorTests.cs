using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.AsyncApi.Config;
using Ratatoskr.AsyncApi.Extensions;
using Ratatoskr.AsyncApi.Generation;
using Ratatoskr.AsyncApi.Model.Bindings;
using Ratatoskr.CloudEvents;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.AsyncApi;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.Tests.AsyncApi;

public class AsyncApiDocumentGeneratorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
            bus.ConfigureAsyncApi(opts =>
            {
                opts.WithTitle("Test Service").WithVersion("1.0.0");
                asyncApiConfig?.Invoke(opts);
            });
            busConfig(bus);
        });

        if (rabbitMqOptions != null)
        {
            services.AddSingleton(rabbitMqOptions);
            services.AddSingleton<IAsyncApiTransportBindingProvider, RabbitMqAsyncApiBindingProvider>();
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
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<ApiKeyRevokedEvent>())
                .AddEventConsumeChannel("user.events", c => c
                    .WithAsyncApi(a => a
                        .WithDescription("Channel for authorization events owned by the user service."))
                    .WithRabbitMq(r => r
                        .WithFanoutExchange()
                        .WithQueueName("apikey.subscriptions"))
                    .Consumes<UserRolesChangedEvent>(m => m
                        .WithAsyncApi(a => a
                            .WithVersion("2.0.0")))),
            asyncApiConfig: opts => opts
                .WithDescription("AsyncAPI documentation for the API Key service."),
            rabbitMqOptions: new RabbitMqOptions { ConnectionString = new Uri("amqp://a:b@rabbitmq.example.com/") });

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
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<OrderCreatedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { ConnectionString = new Uri("amqp://a:b@localhost/") },
            contentMode: CloudEventsContentMode.Structured);

        var document = generator.Generate();
        var json = JsonSerializer.Serialize(document, JsonOptions);

        await Verify(json, extension: "json").UseDirectory("Snapshots");
    }

    [Test]
    public void Generate_WithDataAnnotations_SchemaIncludesConstraints()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("orders.events", c => c
                    .Produces<OrderCreatedEvent>()));

        var document = generator.Generate();

        var orderSchema = document.Components!.Schemas!["OrderCreatedEvent"];

        orderSchema.Should().NotBeNull();
        orderSchema.Properties!["amount"].Minimum.Should().Be(0.01);
        orderSchema.Properties["amount"].Maximum.Should().Be(999999.99);
        orderSchema.Properties["customerEmail"].Format.Should().Be("email");
        orderSchema.Properties["callbackUrl"].Format.Should().Be("uri");
        orderSchema.Properties["notes"].MaxLength.Should().Be(500);
        orderSchema.Properties["notes"].MinLength.Should().Be(1);
        orderSchema.Required.Should().Contain("orderId");
        orderSchema.Required.Should().Contain("amount");
    }

    [Test]
    public void Generate_WithAsyncApiMessageAttribute_UsesAttributeMetadata()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("apikey.events", c => c
                    .Produces<ApiKeyRevokedEvent>()));

        var document = generator.Generate();

        var message = document.Components!.Messages!["api-key.revoked"];

        message.Title.Should().Be("API Key Revoked");
        message.Description.Should().Be("An API key has been revoked.");
        message.Extensions!["x-eventcatalog-message-version"].GetString().Should().Be("1.0.0");
        message.Extensions["x-eventcatalog-message-type"].GetString().Should().Be("event");
        message.Extensions["x-eventcatalog-role"].GetString().Should().Be("provider");
    }

    [Test]
    public void Generate_ConsumerChannel_RoleIsClient()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithRabbitMq(r => r.WithQueueName("my.queue"))
                    .Consumes<UserRolesChangedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { ConnectionString = new Uri("amqp://a:b@localhost/") });

        var document = generator.Generate();

        var message = document.Components!.Messages!["user-roles-changed"];
        message.Extensions!["x-eventcatalog-role"].GetString().Should().Be("client");
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

        document.Servers.Should().BeNull();
        await Verify(json, extension: "json").UseDirectory("Snapshots");
    }

    [Test]
    public void Generate_MultipleMessagesOnChannel_DefaultPerMessageOperations()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("events", c => c
                    .Produces<ApiKeyRevokedEvent>()
                    .Produces<OrderCreatedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { ConnectionString = new Uri("amqp://a:b@localhost/") });

        var document = generator.Generate();

        // Channel still has both messages
        document.Channels["events"].Messages!.Count.Should().Be(2);
        document.Components!.Messages.Should().ContainKey("api-key.revoked");
        document.Components!.Messages.Should().ContainKey("order.created");

        // But now there are two separate operations (one per message)
        document.Operations.Count.Should().Be(2);
        document.Operations.Should().ContainKey("sendApiKeyRevokedEvent");
        document.Operations.Should().ContainKey("sendOrderCreatedEvent");
        document.Operations["sendApiKeyRevokedEvent"].Messages!.Count.Should().Be(1);
        document.Operations["sendOrderCreatedEvent"].Messages!.Count.Should().Be(1);
    }

    [Test]
    public void Generate_RabbitMqQueueChannel_IsAddedForConsumeChannels()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithRabbitMq(r => r
                        .WithFanoutExchange()
                        .WithQueueName("apikey.subscriptions"))
                    .Consumes<UserRolesChangedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { ConnectionString = new Uri("amqp://a:b@localhost/") });

        var document = generator.Generate();

        document.Channels.Should().ContainKey("user.events");
        document.Channels.Should().ContainKey("apikey.subscriptions");

        var queueChannel = document.Channels["apikey.subscriptions"];
        queueChannel.Bindings!.Amqp!.Is.Should().Be(AmqpChannelType.Queue);
        queueChannel.Bindings.Amqp.Queue!.Name.Should().Be("apikey.subscriptions");
    }

    [Test]
    public void Generate_ChannelLevelOperation_GroupsAllMessages()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("order.events", c => c
                    .WithAsyncApi(a => a.WithOperation(o => o.WithTitle("Publish Order Events")))
                    .Produces<OrderCreatedEvent>()
                    .Produces<ApiKeyRevokedEvent>()));

        var document = generator.Generate();

        // Single grouped operation using channel name as key
        document.Operations.Count.Should().Be(1);
        document.Operations.Should().ContainKey("order.events");
        document.Operations["order.events"].Title.Should().Be("Publish Order Events");
        document.Operations["order.events"].Messages!.Count.Should().Be(2);
    }

    [Test]
    public void Generate_ChannelLevelOperationId_CanBeOverridden()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventConsumeChannel("user.events", c => c
                    .WithAsyncApi(a => a.WithOperation(o => o.WithId("partner-api-key-revoke")))
                    .Consumes<UserRolesChangedEvent>()));

        var document = generator.Generate();

        document.Operations.Should().ContainKey("partner-api-key-revoke");
        document.Operations.Should().NotContainKey("user.events");
    }

    [Test]
    public void Generate_SharedOperationId_MergesMessagesIntoOneOperation()
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
        document.Operations.Should().ContainKey("consumeOrderLifecycle");
        document.Operations["consumeOrderLifecycle"].Messages!.Count.Should().Be(2);
        document.Operations["consumeOrderLifecycle"].Title.Should().Be("Consume Order Lifecycle");

        // Third message gets its own operation
        document.Operations.Should().ContainKey("receiveUserRolesChangedEvent");
        document.Operations["receiveUserRolesChangedEvent"].Messages!.Count.Should().Be(1);

        document.Operations.Count.Should().Be(2);
    }

    [Test]
    public void Generate_PerMessageOperationCustomization()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("order.events", c => c
                    .Produces<OrderCreatedEvent>(m => m
                        .WithAsyncApi(a => a.WithOperation(o => o
                            .WithDescription("Emitted when a new order is placed."))))));

        var document = generator.Generate();

        document.Operations.Should().ContainKey("sendOrderCreatedEvent");
        document.Operations["sendOrderCreatedEvent"].Description
            .Should().Be("Emitted when a new order is placed.");
    }

    [Test]
    public void Generate_DuplicateOperationId_AcrossChannels_Throws()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("channel1", c => c
                    .Produces<OrderCreatedEvent>())
                .AddEventPublishChannel("channel2", c => c
                    .Produces<OrderCreatedEvent>()));

        // Same message type on two channels → both default to "sendOrderCreatedEvent"
        var act = () => generator.Generate();
        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Generate_PublishAndConsumeOnSameChannel_NoDuplicateServerRefs()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("events.topic", c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<OrderCreatedEvent>())
                .AddEventConsumeChannel("events.topic", c => c
                    .WithRabbitMq(r => r
                        .WithQueueName("events.subscriptions"))
                    .Consumes<OrderCreatedEvent>()),
            rabbitMqOptions: new RabbitMqOptions { ConnectionString = new Uri("amqp://a:b@localhost/") });

        var document = generator.Generate();

        var channel = document.Channels["events.topic"];
        channel.Servers.Should().NotBeNull();
        channel.Servers!.Count.Should().Be(1);
    }

    [Test]
    public void Generate_OperationTags_IncludedInOutput()
    {
        var generator = BuildGenerator(
            bus => bus
                .AddEventPublishChannel("order.events", c => c
                    .Produces<OrderCreatedEvent>(m => m
                        .WithAsyncApi(a => a.WithOperation(o => o
                            .WithTags("orders", "lifecycle"))))));

        var document = generator.Generate();

        var op = document.Operations["sendOrderCreatedEvent"];
        op.Tags.Should().NotBeNull();
        op.Tags!.Count.Should().Be(2);
        op.Tags[0].Name.Should().Be("orders");
        op.Tags[1].Name.Should().Be("lifecycle");
    }
}
