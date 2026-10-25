using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Content type assumed for message data when none is set.</summary>
    private const string DefaultDataContentType = "application/json";

    /// <summary>The header prefixes that mark a CloudEvents attribute, preferred one first.</summary>
    private static readonly string[] _cloudEventHeaderPrefixes =
    [
        CloudEventsAmqpConstants.HeaderPrefix,
        CloudEventsAmqpConstants.AlternativeHeaderPrefix,
    ];

    /// <summary>
    /// CloudEvents attributes that <see cref="MapBinaryModeIncoming"/> maps to dedicated
    /// <see cref="MessageProperties"/> fields rather than to extensions.
    /// </summary>
    private static readonly FrozenSet<string> _attributesMappedToMessageProperties = new[]
    {
        CloudEventsAmqpConstants.SpecVersionHeader,
        CloudEventsAmqpConstants.IdHeader,
        CloudEventsAmqpConstants.TypeHeader,
        CloudEventsAmqpConstants.SourceHeader,
        CloudEventsAmqpConstants.TimeHeader,
        CloudEventsAmqpConstants.SubjectHeader,
        CloudEventsAmqpConstants.DataContentTypeHeader,
        CloudEventsAmqpConstants.DataSchemaHeader,
        CloudEventsAmqpConstants.TraceParentHeader,
        CloudEventsAmqpConstants.TraceStateHeader,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps outgoing message data and properties onto AMQP basic properties using the configured CloudEvents content mode.
    /// </summary>
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

        // Content mode is configured per-mapper instance. Per-message override tracked in issue backlog.
        return options.ContentMode switch
        {
            CloudEventsContentMode.Binary => MapBinaryMode(serializedData, props, outgoing),
            CloudEventsContentMode.Structured => MapStructuredMode(serializedData, props, outgoing),
            _ => throw new NotSupportedException(
                $"Unsupported {nameof(CloudEventsContentMode)}: {options.ContentMode}"
            ),
        };
    }

    /// <summary>
    /// Maps an incoming AMQP delivery to raw message bytes and <see cref="MessageProperties"/>, auto-detecting binary or structured CloudEvents content mode.
    /// </summary>
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

        return MapBinaryModeIncoming(incoming);
    }

    private static byte[] MapBinaryMode(
        byte[] serializedData,
        MessageProperties props,
        BasicProperties outgoing
    )
    {
        var dataContentType = props.ContentType ?? DefaultDataContentType;
        SetStandardAmqpProperties(outgoing, props, dataContentType);

        // Initialize headers if needed
        outgoing.Headers ??= new Dictionary<string, object?>(StringComparer.Ordinal);

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
        SetCloudEventHeader(outgoing.Headers, "datacontenttype", dataContentType);

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
            SetCloudEventHeader(outgoing.Headers, ext.Key, ext.Value.ToString());
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
            if (!TryGetCloudEventAttributeName(header.Key, out _))
            {
                outgoing.Headers[header.Key] = header.Value;
            }
        }

        // Trace propagation: RabbitMQ.Client 7.x sets the W3C traceparent/tracestate
        // AMQP headers automatically from Activity.Current during BasicPublishAsync.
        // Setting them here would be overwritten by the client, so we delegate entirely.

        return serializedData;
    }

    private static byte[] MapStructuredMode(
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
                ? new Dictionary<string, object>(props.CloudEventExtensions, StringComparer.Ordinal)
                : null;

        if (!string.IsNullOrWhiteSpace(props.TraceParent))
        {
            extensions ??= new Dictionary<string, object>(StringComparer.Ordinal);
            extensions[CloudEventsAmqpConstants.TraceParentHeader] = props.TraceParent;
        }

        if (!string.IsNullOrWhiteSpace(props.TraceState))
        {
            extensions ??= new Dictionary<string, object>(StringComparer.Ordinal);
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

        SetStandardAmqpProperties(outgoing, props, CloudEventsAmqpConstants.JsonContentType);

        // Copy custom headers
        if (props.Headers.Count > 0)
        {
            outgoing.Headers = new Dictionary<string, object?>(StringComparer.Ordinal);
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
        var incomingHeaders =
            incoming.BasicProperties.Headers
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);

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
        if (
            timeStr != null
            && DateTimeOffset.TryParse(timeStr, CultureInfo.InvariantCulture, out var parsedTime)
        )
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
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in incomingHeaders)
        {
            headers[header.Key] = RabbitMqHeaderHelper.ConvertHeaderToString(header.Value);
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
            if (
                TryGetCloudEventAttributeName(kv.Key, out var attrName)
                && !_attributesMappedToMessageProperties.Contains(attrName)
            )
            {
                props.CloudEventExtensions[attrName] = RabbitMqHeaderHelper.ConvertHeaderToString(
                    kv.Value
                );
            }
        }
    }

    private static (byte[] body, MessageProperties props) MapStructuredModeIncoming(
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

        var incomingHeaders =
            incoming.BasicProperties.Headers
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        cloudEvent.TryGetExtension<string>(
            CloudEventsAmqpConstants.TraceParentHeader,
            out var envelopeTraceParent
        );
        cloudEvent.TryGetExtension<string>(
            CloudEventsAmqpConstants.TraceStateHeader,
            out var envelopeTraceState
        );
        var (traceParent, traceState) = ResolveTraceContext(
            (envelopeTraceParent, envelopeTraceState),
            GetBareTraceContextHeaders(incomingHeaders),
            GetCloudEventTraceContextHeaders(incomingHeaders)
        );

        var props = new MessageProperties
        {
            Id = cloudEvent.Id,
            Type = cloudEvent.Type,
            Source = cloudEvent.Source,
            Time = cloudEvent.Time,
            ContentType = cloudEvent.DataContentType ?? DefaultDataContentType,
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
                props.Headers[header.Key] = RabbitMqHeaderHelper.ConvertHeaderToString(header.Value);
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
    /// Sets the standard AMQP properties, which non-CloudEvents consumers (e.g. Wolverine) rely on.
    /// </summary>
    private static void SetStandardAmqpProperties(
        BasicProperties outgoing,
        MessageProperties props,
        string contentType
    )
    {
        outgoing.ContentType = contentType;
        outgoing.DeliveryMode = DeliveryModes.Persistent;
        outgoing.MessageId = props.Id;
        outgoing.Timestamp = new AmqpTimestamp(props.Time!.Value.ToUnixTimeSeconds());
        outgoing.Type = props.Type;

        if (!string.IsNullOrEmpty(props.Source))
        {
            outgoing.AppId = props.Source;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="headerKey"/> carries a CloudEvents attribute (either
    /// prefix convention) and outputs the attribute name without the prefix.
    /// </summary>
    private static bool TryGetCloudEventAttributeName(string headerKey, out string attributeName)
    {
        foreach (var prefix in _cloudEventHeaderPrefixes)
        {
            if (headerKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                attributeName = headerKey[prefix.Length..];
                return true;
            }
        }

        attributeName = string.Empty;
        return false;
    }

    /// <summary>
    /// Gets a CloudEvents header, trying both naming conventions (underscore first, then colon).
    /// </summary>
    private static string? GetCloudEventHeader(
        IDictionary<string, object?> headers,
        string attributeName
    )
    {
        foreach (var prefix in _cloudEventHeaderPrefixes)
        {
            if (headers.TryGetValue(prefix + attributeName, out var value))
            {
                return RabbitMqHeaderHelper.ConvertHeaderToString(value);
            }
        }

        return null;
    }

    private static (string? TraceParent, string? TraceState) GetCloudEventTraceContextHeaders(
        IDictionary<string, object?> headers
    ) =>
        (
            GetCloudEventHeader(headers, CloudEventsAmqpConstants.TraceParentHeader),
            GetCloudEventHeader(headers, CloudEventsAmqpConstants.TraceStateHeader)
        );

    /// <summary>The W3C trace context headers set by RabbitMQ.Client for the transport span.</summary>
    private static (string? TraceParent, string? TraceState) GetBareTraceContextHeaders(
        IDictionary<string, object?> headers
    ) =>
        (
            GetHeaderValue(headers, CloudEventsAmqpConstants.TraceParentHeader),
            GetHeaderValue(headers, CloudEventsAmqpConstants.TraceStateHeader)
        );

    private static (string? traceParent, string? traceState) ResolveTraceContextFromBinaryHeaders(
        IDictionary<string, object?> headers
    ) =>
        ResolveTraceContext(
            GetCloudEventTraceContextHeaders(headers),
            GetBareTraceContextHeaders(headers)
        );

    /// <summary>
    /// Returns the first candidate with a valid trace context, in order of preference. If none is
    /// valid, falls back to the first non-null trace parent and trace state.
    /// </summary>
    private static (string? traceParent, string? traceState) ResolveTraceContext(
        params ReadOnlySpan<(string? TraceParent, string? TraceState)> candidates
    )
    {
        foreach (var candidate in candidates)
        {
            if (TryParseTraceContext(candidate.TraceParent, candidate.TraceState))
            {
                return candidate;
            }
        }

        string? traceParent = null;
        string? traceState = null;
        foreach (var candidate in candidates)
        {
            traceParent ??= candidate.TraceParent;
            traceState ??= candidate.TraceState;
        }

        return (traceParent, traceState);
    }

    private static bool TryParseTraceContext(string? traceParent, string? traceState)
    {
        return !string.IsNullOrWhiteSpace(traceParent)
            && ActivityContext.TryParse(traceParent, traceState, out _);
    }

    private static string? GetHeaderValue(IDictionary<string, object?> headers, string key)
    {
        return headers.TryGetValue(key, out var value)
            ? RabbitMqHeaderHelper.ConvertHeaderToString(value)
            : null;
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
