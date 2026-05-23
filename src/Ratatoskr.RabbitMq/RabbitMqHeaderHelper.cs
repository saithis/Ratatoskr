using System.Text;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Helper methods for working with RabbitMQ message headers.
/// </summary>
internal static class RabbitMqHeaderHelper
{
    /// <summary>
    /// Converts various header value types to string representation.
    /// </summary>
    public static string ConvertHeaderToString(object? value)
    {
        return value switch
        {
            null => "",
            string str => str,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>
    /// Extracts the original exchange and routing key from RabbitMQ's x-death header.
    /// This header is automatically added by RabbitMQ when messages go through dead-letter exchanges.
    /// </summary>
    public static (string? exchange, string? routingKey) GetOriginalDestinationFromHeaders(
        IDictionary<string, object?>? headers
    )
    {
        if (headers == null || !headers.TryGetValue("x-death", out var xDeathObj))
        {
            return (null, null);
        }

        // x-death is an array of death records, we want the first (most recent) one
        if (xDeathObj is System.Collections.IEnumerable xDeathList)
        {
            foreach (var entryObj in xDeathList)
            {
                if (entryObj is IDictionary<string, object> entry)
                {
                    // Extract exchange
                    string? exchange = null;
                    if (entry.TryGetValue("exchange", out var exchObj))
                    {
                        exchange = ConvertHeaderToString(exchObj);
                    }

                    // Extract routing keys (it's an array, take the first one)
                    string? routingKey = null;
                    if (
                        entry.TryGetValue("routing-keys", out var rkObj)
                        && rkObj is System.Collections.IEnumerable routingKeys
                    )
                    {
                        foreach (var rk in routingKeys)
                        {
                            routingKey = ConvertHeaderToString(rk);
                            break; // Take first routing key
                        }
                    }

                    // Return the first entry we find that has a non-empty exchange
                    if (!string.IsNullOrEmpty(exchange))
                    {
                        return (exchange, routingKey);
                    }
                }
            }
        }

        return (null, null);
    }
}
