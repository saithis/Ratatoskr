using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Default implementation of IRabbitMqEnvelopeMapper that follows the CloudEvents AMQP protocol binding.
/// See: https://github.com/cloudevents/spec/blob/main/cloudevents/bindings/amqp-protocol-binding.md
/// </summary>
public partial class CloudEventsAmqpMapper(
    CloudEventsOptions options,
    ILogger<CloudEventsAmqpMapper> logger
) : IRabbitMqEnvelopeMapper
{
    public byte[] MapOutgoing(
        byte[] serializedData,
        MessageProperties props,
        BasicProperties outgoing
    )
    {
        ArgumentNullException.ThrowIfNull(serializedData);
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(outgoing);

        // Ensure required CloudEvents fields are set
        if (string.IsNullOrEmpty(props.Id))
        {
            throw new InvalidOperationException("CloudEvents 'id' is required but not set.");
        }
        if (string.IsNullOrEmpty(props.Source))
        {
            throw new InvalidOperationException("CloudEvents 'source' is required but not set.");
        }
        if (props.Time is null)
        {
            throw new InvalidOperationException("CloudEvents 'time' is required but not set.");
        }
        if (string.IsNullOrEmpty(props.Type))
        {
            throw new InvalidOperationException(
                "CloudEvents 'type' is required but not set. "
                    + "Either register the message type or set MessageProperties.Type explicitly."
            );
        }

        // TODO: make this settable per message and maybe add it to the properties
        return options.ContentMode switch
        {
            CloudEventsContentMode.Binary => MapBinaryMode(serializedData, props, outgoing),
            CloudEventsContentMode.Structured => MapStructuredMode(serializedData, props, outgoing),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    public (byte[] body, MessageProperties props) MapIncoming(BasicDeliverEventArgs incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        // Detect content mode based on content type
        var contentType = incoming.BasicProperties.ContentType;
        var isStructured =
            contentType?.StartsWith(
                CloudEventsAmqpConstants.JsonContentType,
                StringComparison.OrdinalIgnoreCase
            ) ?? false;

        if (isStructured)
        {
            return MapStructuredModeIncoming(incoming);
        }
        else
        {
            return MapBinaryModeIncoming(incoming);
        }
    }

    private byte[] MapBinaryMode(
        byte[] serializedData,
        MessageProperties props,
        BasicProperties outgoing
    )
    {
        // Set standard RabbitMQ properties (for Wolverine compatibility)
        outgoing.ContentType = props.ContentType ?? "application/json";
        outgoing.DeliveryMode = DeliveryModes.Persistent;
        outgoing.MessageId = props.Id;
        outgoing.Timestamp = new AmqpTimestamp(props.Time!.Value.ToUnixTimeSeconds());
        outgoing.Type = props.Type;

        if (!string.IsNullOrEmpty(props.Source))
        {
            outgoing.AppId = props.Source;
        }

        // Initialize headers if needed
        outgoing.Headers ??= new Dictionary<string, object?>();

        // Set CloudEvents attributes as headers (AMQP binding spec)
        SetCloudEventHeader(outgoing.Headers, "specversion", CloudEventsAmqpConstants.SpecVersion);
        SetCloudEventHeader(outgoing.Headers, "id", props.Id);
        SetCloudEventHeader(outgoing.Headers, "type", props.Type);
        SetCloudEventHeader(outgoing.Headers, "source", props.Source);
        SetCloudEventHeader(
            outgoing.Headers,
            "time",
            props.Time!.Value.ToString("O", CultureInfo.InvariantCulture)
        );
        SetCloudEventHeader(
            outgoing.Headers,
            "datacontenttype",
            props.ContentType ?? "application/json"
        );

        if (!string.IsNullOrEmpty(props.Subject))
        {
            SetCloudEventHeader(outgoing.Headers, "subject", props.Subject);
        }

        if (!string.IsNullOrEmpty(props.DataSchema))
        {
            SetCloudEventHeader(
                outgoing.Headers,
                CloudEventsAmqpConstants.DataSchemaHeader,
                props.DataSchema
            );
        }

        // Add CloudEvent extensions as headers
        foreach (var ext in props.CloudEventExtensions)
        {
            SetCloudEventHeader(outgoing.Headers, ext.Key, ext.Value?.ToString());
        }

        // Preserve Ratatoskr's own trace context in CloudEvents-prefixed headers.
        // RabbitMQ.Client may add/override bare traceparent/tracestate headers with
        // transport-level span context; the CloudEvents headers keep logical message
        // continuity for Ratatoskr spans across producer/consumer boundaries.
        SetCloudEventHeader(
            outgoing.Headers,
            CloudEventsAmqpConstants.TraceParentHeader,
            props.TraceParent
        );
        SetCloudEventHeader(
            outgoing.Headers,
            CloudEventsAmqpConstants.TraceStateHeader,
            props.TraceState
        );

        // Add custom headers (non-CloudEvents)
        foreach (var header in props.Headers)
        {
            if (
                !header.Key.StartsWith(CloudEventsAmqpConstants.HeaderPrefix)
                && !header.Key.StartsWith(CloudEventsAmqpConstants.AlternativeHeaderPrefix)
            )
            {
                outgoing.Headers[header.Key] = header.Value;
            }
        }

        // Trace propagation: RabbitMQ.Client 7.x sets the W3C traceparent/tracestate
        // AMQP headers automatically from Activity.Current during BasicPublishAsync.
        // Setting them here would be overwritten by the client, so we delegate entirely.

        return serializedData;
    }

    private byte[] MapStructuredMode(
        byte[] serializedData,
        MessageProperties props,
        BasicProperties outgoing
    )
    {
        // Deserialize data to embed in envelope
        object? data;
        try
        {
            data = JsonSerializer.Deserialize<object>(serializedData);
        }
        catch
        {
            // If deserialization fails, embed as base64 string
            data = Convert.ToBase64String(serializedData);
        }

        var extensions =
            props.CloudEventExtensions.Count > 0
                ? new Dictionary<string, object>(props.CloudEventExtensions)
                : null;

        if (!string.IsNullOrWhiteSpace(props.TraceParent))
        {
            extensions ??= new Dictionary<string, object>();
            extensions[CloudEventsAmqpConstants.TraceParentHeader] = props.TraceParent;
        }

        if (!string.IsNullOrWhiteSpace(props.TraceState))
        {
            extensions ??= new Dictionary<string, object>();
            extensions[CloudEventsAmqpConstants.TraceStateHeader] = props.TraceState;
        }

        var cloudEvent = new CloudEventEnvelope
        {
            Id = props.Id!,
            Source = props.Source!,
            Type = props.Type!,
            Time = props.Time,
            DataContentType = props.ContentType,
            DataSchema = props.DataSchema,
            Subject = props.Subject,
            Data = data,
            Extensions = extensions,
        };

        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(cloudEvent);

        // Set properties
        outgoing.ContentType = CloudEventsAmqpConstants.JsonContentType;
        outgoing.DeliveryMode = DeliveryModes.Persistent;
        outgoing.MessageId = props.Id;
        outgoing.Timestamp = new AmqpTimestamp(props.Time!.Value.ToUnixTimeSeconds());

        // Optionally set standard RabbitMQ properties too (for compatibility)
        outgoing.Type = props.Type;
        if (!string.IsNullOrEmpty(props.Source))
        {
            outgoing.AppId = props.Source;
        }

        // Copy custom headers
        if (props.Headers.Count > 0)
        {
            outgoing.Headers = new Dictionary<string, object?>();
            foreach (var header in props.Headers)
            {
                outgoing.Headers[header.Key] = header.Value;
            }
        }

        // Trace propagation: RabbitMQ.Client 7.x sets the W3C traceparent/tracestate
        // AMQP headers automatically from Activity.Current during BasicPublishAsync.
        // Setting them here would be overwritten by the client, so we delegate entirely.

        return envelopeBytes;
    }

    private (byte[] body, MessageProperties props) MapBinaryModeIncoming(
        BasicDeliverEventArgs incoming
    )
    {
        var incomingHeaders = incoming.BasicProperties.Headers ?? new Dictionary<string, object?>();

        // Prefer standard RabbitMQ properties over CloudEvents headers (Wolverine compatibility)
        var id =
            incoming.BasicProperties.MessageId
            ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.IdHeader);
        if (id is null)
        {
            id = Guid.NewGuid().ToString();
            LogIncomingBinaryCloudEventHasNoId(logger, id);
        }

        var type =
            incoming.BasicProperties.Type
            ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TypeHeader)
            ?? "";

        var source =
            incoming.BasicProperties.AppId
            ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.SourceHeader)
            ?? "/";

        // Parse time from CloudEvents header if available
        DateTimeOffset? time = null;
        var timeStr = GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TimeHeader);
        if (timeStr != null && DateTimeOffset.TryParse(timeStr, out var parsedTime))
        {
            time = parsedTime;
        }
        else if (incoming.BasicProperties.Timestamp.UnixTime > 0)
        {
            time = DateTimeOffset.FromUnixTimeSeconds(incoming.BasicProperties.Timestamp.UnixTime);
        }

        var contentType =
            incoming.BasicProperties.ContentType
            ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.DataContentTypeHeader);

        var subject = GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.SubjectHeader);
        var dataSchema = GetCloudEventHeader(
            incomingHeaders,
            CloudEventsAmqpConstants.DataSchemaHeader
        );

        var (traceParent, traceState) = ResolveTraceContextFromBinaryHeaders(incomingHeaders);

        // Build headers dictionary (include all headers)
        var headers = new Dictionary<string, string>();
        foreach (var header in incomingHeaders)
        {
            headers[header.Key] = ConvertToString(header.Value);
        }

        var props = new MessageProperties
        {
            Id = id,
            Type = type,
            Source = source,
            Time = time,
            ContentType = contentType,
            DataSchema = dataSchema,
            Subject = subject,
            Headers = headers,
            TraceParent = traceParent,
            TraceState = traceState,
        };

        CopyBinaryModeCloudEventExtensionsFromHeaders(incomingHeaders, props);

        return (incoming.Body.ToArray(), props);
    }

    /// <summary>
    /// Binary mode outgoing maps <see cref="MessageProperties.CloudEventExtensions"/> to <c>cloudEvents_*</c> AMQP headers.
    /// Incoming must reconstruct those attributes into <see cref="MessageProperties.CloudEventExtensions"/> so inbox / observers
    /// see custom extensions (they are not duplicated onto core <see cref="MessageProperties"/> fields).
    /// </summary>
    private static void CopyBinaryModeCloudEventExtensionsFromHeaders(
        IDictionary<string, object?> incomingHeaders,
        MessageProperties props
    )
    {
        foreach (var kv in incomingHeaders)
        {
            string? attrName = null;
            if (kv.Key.StartsWith(CloudEventsAmqpConstants.HeaderPrefix, StringComparison.Ordinal))
            {
                attrName = kv.Key[CloudEventsAmqpConstants.HeaderPrefix.Length..];
            }
            else if (
                kv.Key.StartsWith(
                    CloudEventsAmqpConstants.AlternativeHeaderPrefix,
                    StringComparison.Ordinal
                )
            )
            {
                attrName = kv.Key[CloudEventsAmqpConstants.AlternativeHeaderPrefix.Length..];
            }

            if (attrName is null)
            {
                continue;
            }

            if (IsBinaryModeCloudEventsAttributeMappedToMessageProperties(attrName))
            {
                continue;
            }

            props.CloudEventExtensions[attrName] = ConvertToString(kv.Value);
        }
    }

    /// <summary>Attribute names that <see cref="MapBinaryModeIncoming"/> maps to <see cref="MessageProperties"/>, not to extensions.</summary>
    private static bool IsBinaryModeCloudEventsAttributeMappedToMessageProperties(
        string attributeName
    ) =>
        attributeName.Equals(
            CloudEventsAmqpConstants.SpecVersionHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.IdHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.TypeHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.SourceHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.TimeHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.SubjectHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.DataContentTypeHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.DataSchemaHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.TraceParentHeader,
            StringComparison.OrdinalIgnoreCase
        )
        || attributeName.Equals(
            CloudEventsAmqpConstants.TraceStateHeader,
            StringComparison.OrdinalIgnoreCase
        );

    private (byte[] body, MessageProperties props) MapStructuredModeIncoming(
        BasicDeliverEventArgs incoming
    )
    {
        // Parse CloudEvents envelope
        var cloudEvent =
            JsonSerializer.Deserialize<CloudEventEnvelope>(incoming.Body.ToArray())
            ?? throw new InvalidOperationException("Failed to deserialize CloudEvents envelope");

        // Extract data and re-serialize it for the deserializer
        byte[] dataBytes;
        if (cloudEvent.Data != null)
        {
            dataBytes = JsonSerializer.SerializeToUtf8Bytes(cloudEvent.Data);
        }
        else
        {
            dataBytes = Array.Empty<byte>();
        }

        var incomingHeaders = incoming.BasicProperties.Headers ?? new Dictionary<string, object?>();
        cloudEvent.TryGetExtension<string>(
            CloudEventsAmqpConstants.TraceParentHeader,
            out var envelopeTraceParent
        );
        cloudEvent.TryGetExtension<string>(
            CloudEventsAmqpConstants.TraceStateHeader,
            out var envelopeTraceState
        );
        var (traceParent, traceState) = ResolveTraceContextWithFallback(
            envelopeTraceParent,
            envelopeTraceState,
            GetHeaderValue(incomingHeaders, CloudEventsAmqpConstants.TraceParentHeader),
            GetHeaderValue(incomingHeaders, CloudEventsAmqpConstants.TraceStateHeader),
            GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TraceParentHeader),
            GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TraceStateHeader)
        );

        var props = new MessageProperties
        {
            Id = cloudEvent.Id,
            Type = cloudEvent.Type,
            Source = cloudEvent.Source,
            Time = cloudEvent.Time,
            ContentType = cloudEvent.DataContentType ?? "application/json",
            DataSchema = cloudEvent.DataSchema,
            Subject = cloudEvent.Subject,
            TraceParent = traceParent,
            TraceState = traceState,
        };

        // Copy extensions to CloudEventExtensions
        if (cloudEvent.Extensions != null)
        {
            foreach (var ext in cloudEvent.Extensions)
            {
                props.CloudEventExtensions[ext.Key] = ext.Value;
            }
        }

        // Include RabbitMQ headers (for custom metadata)
        if (incoming.BasicProperties.Headers != null)
        {
            foreach (var header in incoming.BasicProperties.Headers)
            {
                props.Headers[header.Key] = ConvertToString(header.Value);
            }
        }

        return (dataBytes, props);
    }

    /// <summary>
    /// Sets a CloudEvents header using the underscore prefix (cloudEvents_).
    /// Omits the header if the value is null.
    /// </summary>
    private static void SetCloudEventHeader(
        IDictionary<string, object?> headers,
        string attributeName,
        string? value
    )
    {
        if (value == null)
        {
            return; // Per CloudEvents spec, omit null attributes
        }

        headers[$"{CloudEventsAmqpConstants.HeaderPrefix}{attributeName}"] = value;
    }

    /// <summary>
    /// Gets a CloudEvents header, trying both naming conventions (underscore first, then colon).
    /// </summary>
    private static string? GetCloudEventHeader(
        IDictionary<string, object?> headers,
        string attributeName
    )
    {
        // Try underscore format first (cloudEvents_*)
        if (
            headers.TryGetValue(
                $"{CloudEventsAmqpConstants.HeaderPrefix}{attributeName}",
                out var value
            )
        )
        {
            return ConvertToString(value);
        }

        // Try colon format (cloudEvents:*)
        if (
            headers.TryGetValue(
                $"{CloudEventsAmqpConstants.AlternativeHeaderPrefix}{attributeName}",
                out value
            )
        )
        {
            return ConvertToString(value);
        }

        return null;
    }

    private static (string? traceParent, string? traceState) ResolveTraceContextFromBinaryHeaders(
        IDictionary<string, object?> headers
    )
    {
        return ResolveTraceContextWithFallback(
            GetCloudEventHeader(headers, CloudEventsAmqpConstants.TraceParentHeader),
            GetCloudEventHeader(headers, CloudEventsAmqpConstants.TraceStateHeader),
            GetHeaderValue(headers, CloudEventsAmqpConstants.TraceParentHeader),
            GetHeaderValue(headers, CloudEventsAmqpConstants.TraceStateHeader)
        );
    }

    private static (string? traceParent, string? traceState) ResolveTraceContextWithFallback(
        string? preferredTraceParent,
        string? preferredTraceState,
        string? fallbackTraceParent,
        string? fallbackTraceState,
        string? additionalTraceParent = null,
        string? additionalTraceState = null
    )
    {
        if (TryParseTraceContext(preferredTraceParent, preferredTraceState))
        {
            return (preferredTraceParent, preferredTraceState);
        }

        if (TryParseTraceContext(fallbackTraceParent, fallbackTraceState))
        {
            return (fallbackTraceParent, fallbackTraceState);
        }

        if (TryParseTraceContext(additionalTraceParent, additionalTraceState))
        {
            return (additionalTraceParent, additionalTraceState);
        }

        return (
            preferredTraceParent ?? fallbackTraceParent ?? additionalTraceParent,
            preferredTraceState ?? fallbackTraceState ?? additionalTraceState
        );
    }

    private static bool TryParseTraceContext(string? traceParent, string? traceState)
    {
        return !string.IsNullOrWhiteSpace(traceParent)
            && ActivityContext.TryParse(traceParent, traceState, out _);
    }

    private static string? GetHeaderValue(IDictionary<string, object?> headers, string key)
    {
        return headers.TryGetValue(key, out var value) ? ConvertToString(value) : null;
    }

    /// <summary>
    /// Converts header values to strings (handles byte arrays from RabbitMQ).
    /// </summary>
    private static string ConvertToString(object? value)
    {
        return value switch
        {
            null => "",
            string str => str,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> readOnlyMemory => Encoding.UTF8.GetString(readOnlyMemory.Span),
            Memory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            _ => value.ToString() ?? "",
        };
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Incoming binary CloudEvent has no id (AMQP message-id and cloudEvents_id are missing). Generated id {GeneratedEventId}. Per CloudEvents spec, id is required; inbox deduplication will not treat replays of this message as duplicates."
    )]
    private static partial void LogIncomingBinaryCloudEventHasNoId(
        ILogger logger,
        string generatedEventId
    );
}
