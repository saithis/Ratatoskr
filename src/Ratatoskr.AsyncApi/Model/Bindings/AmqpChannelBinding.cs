using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model.Bindings;

public class ChannelBindings
{
    /// <summary>
    /// This object contains information about the channel representation in AMQP.
    /// </summary>
    [JsonPropertyName("amqp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpChannelBinding? Amqp { get; set; }
}

public class AmqpChannelBinding
{
    /// <summary>
    /// Defines what type of channel is it. Can be either <c>queue</c> or <c>routingKey</c> (default).
    /// </summary>
    [JsonPropertyName("is")]
    public string Is { get; set; } = "routingKey";

    /// <summary>
    /// When <c>is</c>=<c>routingKey</c>, this object defines the exchange properties.
    /// </summary>
    [JsonPropertyName("exchange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpExchangeDefinition? Exchange { get; set; }

    /// <summary>
    /// When <c>is</c>=<c>queue</c>, this object defines the queue properties.
    /// </summary>
    [JsonPropertyName("queue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpQueueDefinition? Queue { get; set; }

    /// <summary>
    /// The version of this binding. If omitted, "latest" MUST be assumed.
    /// </summary>
    [JsonPropertyName("bindingVersion")]
    public string BindingVersion { get; set; } = "0.3.0";
}

public class AmqpExchangeDefinition
{
    /// <summary>
    /// The name of the exchange. It MUST NOT exceed 255 characters long.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// The type of the exchange. Can be either <c>topic</c>, <c>direct</c>, <c>fanout</c>, <c>default</c> or <c>headers</c>.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// Whether the exchange should survive broker restarts or not.
    /// </summary>
    [JsonPropertyName("durable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Durable { get; set; }

    /// <summary>
    /// Whether the exchange should be deleted when the last queue is unbound from it.
    /// </summary>
    [JsonPropertyName("autoDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoDelete { get; set; }

    /// <summary>
    /// The virtual host of the exchange. Defaults to <c>/</c>.
    /// </summary>
    [JsonPropertyName("vhost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VHost { get; set; }
}

public class AmqpQueueDefinition
{
    /// <summary>
    /// The name of the queue. It MUST NOT exceed 255 characters long.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Whether the queue should survive broker restarts or not.
    /// </summary>
    [JsonPropertyName("durable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Durable { get; set; }

    /// <summary>
    /// Whether the queue should be used only by one connection or not.
    /// </summary>
    [JsonPropertyName("exclusive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; set; }

    /// <summary>
    /// Whether the queue should be deleted when the last consumer unsubscribes.
    /// </summary>
    [JsonPropertyName("autoDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoDelete { get; set; }

    /// <summary>
    /// The virtual host of the queue. Defaults to <c>/</c>.
    /// </summary>
    [JsonPropertyName("vhost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VHost { get; set; }
}
