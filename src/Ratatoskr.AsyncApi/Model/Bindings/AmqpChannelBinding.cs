using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model.Bindings;

public class ChannelBindings
{
    [JsonPropertyName("amqp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpChannelBinding? Amqp { get; set; }
}

public class AmqpChannelBinding
{
    /// <summary>"routingKey" for exchange-based channels, "queue" for queue-based channels.</summary>
    [JsonPropertyName("is")]
    public string Is { get; set; } = "routingKey";

    [JsonPropertyName("exchange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpExchangeDefinition? Exchange { get; set; }

    [JsonPropertyName("queue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AmqpQueueDefinition? Queue { get; set; }

    [JsonPropertyName("bindingVersion")]
    public string BindingVersion { get; set; } = "0.3.0";
}

public class AmqpExchangeDefinition
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("durable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Durable { get; set; }

    [JsonPropertyName("autoDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoDelete { get; set; }

    [JsonPropertyName("vhost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VHost { get; set; }
}

public class AmqpQueueDefinition
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("durable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Durable { get; set; }

    [JsonPropertyName("exclusive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exclusive { get; set; }

    [JsonPropertyName("autoDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoDelete { get; set; }

    [JsonPropertyName("vhost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VHost { get; set; }
}
