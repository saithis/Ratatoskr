using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal static class LocalTransportMessageSnapshotFactory
{
    public static TransportMessageSnapshot Create(byte[] content, MessageProperties props)
    {
        var headers = new Dictionary<string, object?>();

        if (props.ContentType != null) headers["content-type"] = props.ContentType;
        if (props.Id != null) headers["message-id"] = props.Id;
        if (props.Type != null) headers["type"] = props.Type;
        if (props.Source != null) headers["source"] = props.Source;
        if (props.TraceParent != null) headers["traceparent"] = props.TraceParent;
        if (props.TraceState != null) headers["tracestate"] = props.TraceState;

        foreach (var header in props.Headers)
        {
            headers[header.Key] = header.Value;
        }

        return new TransportMessageSnapshot
        {
            Body = content,
            Headers = headers,
            Metadata = new Dictionary<string, object?>
            {
                ["transport"] = "local",
            },
        };
    }
}
