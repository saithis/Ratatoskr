using System.Globalization;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Default implementation of IRabbitMqEnvelopeMapper that follows the CloudEvents AMQP protocol binding.
/// See: https://github.com/cloudevents/spec/blob/main/cloudevents/bindings/amqp-protocol-binding.md
/// </summary>
public class CloudEventsAmqpMapper(
    CloudEventsOptions options) : IRabbitMqEnvelopeMapper
{
    public byte[] MapOutgoing(byte[] serializedData, MessageProperties props, BasicProperties outgoing)
    {
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
                "CloudEvents 'type' is required but not set. " +
                "Either register the message type or set MessageProperties.Type explicitly.");
        }
        
        // TODO: make this settable per message and maybe add it to the properties
        return options.ContentMode switch
        {
            CloudEventsContentMode.Binary => MapBinaryMode(serializedData, props, outgoing),
            CloudEventsContentMode.Structured => MapStructuredMode(serializedData, props, outgoing),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
    
    public (byte[] body, MessageProperties props) MapIncoming(BasicDeliverEventArgs incoming)
    {
        // Detect content mode based on content type
        var contentType = incoming.BasicProperties.ContentType;
        var isStructured = contentType?.StartsWith(CloudEventsAmqpConstants.JsonContentType, 
            StringComparison.OrdinalIgnoreCase) ?? false;
        
        if (isStructured)
        {
            return MapStructuredModeIncoming(incoming);
        }
        else
        {
            return MapBinaryModeIncoming(incoming);
        }
    }
    
    private byte[] MapBinaryMode(byte[] serializedData, MessageProperties props, BasicProperties outgoing)
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
        SetCloudEventHeader(outgoing.Headers, "time", props.Time!.Value.ToString("O", CultureInfo.InvariantCulture));
        SetCloudEventHeader(outgoing.Headers, "datacontenttype", props.ContentType ?? "application/json");
        
        if (!string.IsNullOrEmpty(props.Subject))
        {
            SetCloudEventHeader(outgoing.Headers, "subject", props.Subject);
        }

        if (!string.IsNullOrEmpty(props.DataSchema))
        {
            SetCloudEventHeader(outgoing.Headers, "dataschema", props.DataSchema);
        }

        // Add CloudEvent extensions as headers
        foreach (var ext in props.CloudEventExtensions)
        {
            SetCloudEventHeader(outgoing.Headers, ext.Key, ext.Value?.ToString());
        }
        
        // Add custom headers (non-CloudEvents)
        foreach (var header in props.Headers)
        {
            if (!header.Key.StartsWith(CloudEventsAmqpConstants.HeaderPrefix) &&
                !header.Key.StartsWith(CloudEventsAmqpConstants.AlternativeHeaderPrefix))
            {
                outgoing.Headers[header.Key] = header.Value;
            }
        }
        
        // Handle trace propagation
        if (!string.IsNullOrEmpty(props.TraceParent))
        {
            outgoing.Headers[CloudEventsAmqpConstants.TraceParentHeader] = props.TraceParent;
            SetCloudEventHeader(outgoing.Headers, "traceparent", props.TraceParent);
        }
        
        if (!string.IsNullOrEmpty(props.TraceState))
        {
            outgoing.Headers[CloudEventsAmqpConstants.TraceStateHeader] = props.TraceState;
            SetCloudEventHeader(outgoing.Headers, "tracestate", props.TraceState);
        }
        
        return serializedData;
    }
    
    private byte[] MapStructuredMode(byte[] serializedData, MessageProperties props, BasicProperties outgoing)
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
            Extensions = props.CloudEventExtensions.Count > 0 ? props.CloudEventExtensions : null
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
        
        // Handle trace propagation for structured mode too (as AMQP headers)
        if (!string.IsNullOrEmpty(props.TraceParent))
        {
            outgoing.Headers ??= new Dictionary<string, object?>();
            outgoing.Headers[CloudEventsAmqpConstants.TraceParentHeader] = props.TraceParent;
        }
        
        if (!string.IsNullOrEmpty(props.TraceState))
        {
            outgoing.Headers ??= new Dictionary<string, object?>();
            outgoing.Headers[CloudEventsAmqpConstants.TraceStateHeader] = props.TraceState;
        }
        
        return envelopeBytes;
    }
    
    private (byte[] body, MessageProperties props) MapBinaryModeIncoming(BasicDeliverEventArgs incoming)
    {
        var incomingHeaders = incoming.BasicProperties.Headers ?? new Dictionary<string, object?>();
        
        // Prefer standard RabbitMQ properties over CloudEvents headers (Wolverine compatibility)
        var id = incoming.BasicProperties.MessageId 
                 ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.IdHeader) 
                 ?? Guid.NewGuid().ToString();
        
        var type = incoming.BasicProperties.Type
                   ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TypeHeader)
                   ?? "";
        
        var source = incoming.BasicProperties.AppId
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
        
        var contentType = incoming.BasicProperties.ContentType
                         ?? GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.DataContentTypeHeader);
        
        var subject = GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.SubjectHeader);
        var dataSchema = GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.DataSchemaHeader);

        // Extract trace context (prefer standard W3C headers, fallback to CloudEvents attributes)
        var traceParent = incomingHeaders.TryGetValue("traceparent", out var tp) 
            ? ConvertToString(tp) 
            : GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TraceParentHeader);
            
        var traceState = incomingHeaders.TryGetValue("tracestate", out var ts)
            ? ConvertToString(ts)
            : GetCloudEventHeader(incomingHeaders, CloudEventsAmqpConstants.TraceStateHeader);
        
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

        return (incoming.Body.ToArray(), props);
    }
    
    private (byte[] body, MessageProperties props) MapStructuredModeIncoming(BasicDeliverEventArgs incoming)
    {
        // Parse CloudEvents envelope
        var cloudEvent = JsonSerializer.Deserialize<CloudEventEnvelope>(incoming.Body.ToArray());
        if (cloudEvent == null)
        {
            throw new InvalidOperationException("Failed to deserialize CloudEvents envelope");
        }
        
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
        if (!cloudEvent.TryGetExtension<string>(CloudEventsAmqpConstants.TraceParentHeader, out string? traceParent))
        {
            traceParent = incomingHeaders.TryGetValue("traceparent", out var tp) 
                ? ConvertToString(tp) : null;
        }
        if (!cloudEvent.TryGetExtension<string>(CloudEventsAmqpConstants.TraceStateHeader, out string? traceState))
        {
            traceState = incomingHeaders.TryGetValue("tracestate", out var ts) 
                ? ConvertToString(ts) : null;
        }

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
    private static void SetCloudEventHeader(IDictionary<string, object?> headers, string attributeName, string? value)
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
    private static string? GetCloudEventHeader(IDictionary<string, object?> headers, string attributeName)
    {
        // Try underscore format first (cloudEvents_*)
        if (headers.TryGetValue($"{CloudEventsAmqpConstants.HeaderPrefix}{attributeName}", out var value))
        {
            return ConvertToString(value);
        }
        
        // Try colon format (cloudEvents:*)
        if (headers.TryGetValue($"{CloudEventsAmqpConstants.AlternativeHeaderPrefix}{attributeName}", out value))
        {
            return ConvertToString(value);
        }
        
        return null;
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
            _ => value.ToString() ?? ""
        };
    }
}
