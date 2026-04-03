using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Tests.RabbitMq;

public class CloudEventsAmqpMapperIncomingTests
{
    private readonly CloudEventsAmqpMapper _mapper = new(new CloudEventsOptions(), NullLogger<CloudEventsAmqpMapper>.Instance);

    [Test]
    public void MapBinaryModeIncoming_ShouldMapTraceContext()
    {
        // Arrange
        var headers = new Dictionary<string, object?>
        {
            { "traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" },
            { "tracestate", "rojo=00f067aa0ba902b7" },
            { "cloudEvents_id", "123" },
            { "cloudEvents_source", "/unit-test" },
            { "cloudEvents_type", "test.event" }
        };

        var basicProperties = new BasicProperties
        {
            Headers = headers,
            ContentType = "application/json"
        };

        var body = Encoding.UTF8.GetBytes("{}");
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.TraceParent.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        result.props.TraceState.Should().Be("rojo=00f067aa0ba902b7");
    }

    [Test]
    public void MapStructuredModeIncoming_ShouldMapTraceContext()
    {
        // Arrange
        var cloudEventJson = "{\"id\":\"123\",\"source\":\"/unit-test\",\"type\":\"test.event\",\"specversion\":\"1.0\",\"data\":{\"foo\":\"bar\"}}";
        var body = Encoding.UTF8.GetBytes(cloudEventJson);

        var headers = new Dictionary<string, object?>
        {
            { "traceparent", "00-structured-trace-id-01" },
            { "tracestate", "structured=true" }
        };

        var basicProperties = new BasicProperties
        {
            Headers = headers,
            ContentType = "application/cloudevents+json"
        };

        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.TraceParent.Should().Be("00-structured-trace-id-01");
        result.props.TraceState.Should().Be("structured=true");
    }

    [Test]
    public void MapIncoming_BinaryMode_ExtractsAllProperties()
    {
        // Arrange
        var headers = new Dictionary<string, object?>
        {
            { "cloudEvents_specversion", "1.0" },
            { "cloudEvents_id", "evt-789" },
            { "cloudEvents_source", "/billing" },
            { "cloudEvents_type", "payment.received" },
            { "cloudEvents_time", "2025-06-15T12:00:00Z" },
            { "cloudEvents_subject", "payment-001" },
            { "cloudEvents_datacontenttype", "application/json" }
        };
        var basicProperties = new BasicProperties
        {
            Headers = headers,
            ContentType = "application/json",
            MessageId = "evt-789",
            Type = "payment.received",
            AppId = "/billing"
        };
        var body = Encoding.UTF8.GetBytes("{\"amount\":100}");
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.Id.Should().Be("evt-789");
        result.props.Source.Should().Be("/billing");
        result.props.Type.Should().Be("payment.received");
        result.props.Subject.Should().Be("payment-001");
        result.props.Time.Should().NotBeNull();
        result.body.Should().BeEquivalentTo(body);
    }

    [Test]
    public void MapIncoming_StructuredMode_ExtractsDataFromEnvelope()
    {
        // Arrange
        var cloudEventJson = JsonSerializer.Serialize(new
        {
            id = "evt-100",
            source = "/inventory",
            type = "item.updated",
            specversion = "1.0",
            time = "2025-06-15T12:00:00Z",
            data = new { itemId = "abc", quantity = 5 }
        });
        var body = Encoding.UTF8.GetBytes(cloudEventJson);
        var basicProperties = new BasicProperties
        {
            ContentType = "application/cloudevents+json"
        };
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert
        result.props.Id.Should().Be("evt-100");
        result.props.Source.Should().Be("/inventory");
        result.props.Type.Should().Be("item.updated");
        result.body.Should().NotBeEmpty();
        var data = JsonSerializer.Deserialize<JsonElement>(result.body);
        data.GetProperty("itemId").GetString().Should().Be("abc");
        data.GetProperty("quantity").GetInt32().Should().Be(5);
    }

    [Test]
    public void MapIncoming_NullHeaders_HandlesGracefully()
    {
        // Arrange - BasicProperties with no headers set (null)
        var basicProperties = new BasicProperties
        {
            ContentType = "application/json"
        };
        var body = Encoding.UTF8.GetBytes("{}");
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert - should not throw and return valid props with defaults
        result.props.Should().NotBeNull();
        result.props.Id.Should().NotBeNullOrEmpty();
        result.body.Should().NotBeEmpty();
    }

    [Test]
    public void MapIncoming_BinaryMode_MissingId_LogsWarning()
    {
        var warnings = new List<string>();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new WarningCaptureLoggerProvider(warnings)));
        var mapper = new CloudEventsAmqpMapper(new CloudEventsOptions(), factory.CreateLogger<CloudEventsAmqpMapper>());

        var basicProperties = new BasicProperties { ContentType = "application/json" };
        var body = Encoding.UTF8.GetBytes("{}");
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        var result = mapper.MapIncoming(incoming);

        result.props.Id.Should().NotBeNullOrEmpty();
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("Incoming binary CloudEvent has no id");
        warnings[0].Should().Contain("inbox deduplication");
    }

    private sealed class WarningCaptureLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new WarningCaptureLogger(sink);
        public void Dispose() { }
    }

    private sealed class WarningCaptureLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                sink.Add(formatter(state, exception));
            }
        }
    }

    [Test]
    public void MapIncoming_EmptyBody_HandlesGracefully()
    {
        // Arrange
        var headers = new Dictionary<string, object?>
        {
            { "cloudEvents_id", "evt-empty" },
            { "cloudEvents_source", "/test" },
            { "cloudEvents_type", "test.event" }
        };
        var basicProperties = new BasicProperties
        {
            Headers = headers,
            ContentType = "application/json"
        };
        var body = Array.Empty<byte>();
        var incoming = new BasicDeliverEventArgs("tag", 1, false, "ex", "rk", basicProperties, body);

        // Act
        var result = _mapper.MapIncoming(incoming);

        // Assert - empty body is passed through as-is in binary mode
        result.body.Should().BeEmpty();
        result.props.Id.Should().Be("evt-empty");
        result.props.Type.Should().Be("test.event");
    }
}
