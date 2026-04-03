using System.Text;
using Ratatoskr.Core;

namespace Ratatoskr.Tests.Fixtures;

public sealed class TestEventPipeMessageSerializer : IMessageSerializer
{
    public string ContentType => "application/x-ratatoskr-testevent-pipe";

    public byte[] Serialize(object message)
    {
        if (message is not TestEvent testEvent)
            throw new InvalidOperationException(
                $"This serializer supports '{nameof(TestEvent)}' only.");

        var encodedId = Convert.ToBase64String(Encoding.UTF8.GetBytes(testEvent.Id ?? string.Empty));
        var encodedData = Convert.ToBase64String(Encoding.UTF8.GetBytes(testEvent.Data ?? string.Empty));
        return Encoding.UTF8.GetBytes($"{encodedId}:{encodedData}");
    }

    public object? Deserialize(byte[] body, Type targetType)
    {
        if (targetType != typeof(TestEvent))
            throw new InvalidOperationException(
                $"This serializer supports '{nameof(TestEvent)}' only.");

        return Deserialize<TestEvent>(body);
    }

    public TMessage? Deserialize<TMessage>(byte[] body)
    {
        if (typeof(TMessage) != typeof(TestEvent))
            throw new InvalidOperationException(
                $"This serializer supports '{nameof(TestEvent)}' only.");

        var payload = Encoding.UTF8.GetString(body);
        var parts = payload.Split(':', 2);
        if (parts.Length != 2)
            throw new InvalidOperationException("Invalid pipe serializer payload.");

        var id = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
        var data = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        return (TMessage?)(object)new TestEvent { Id = id, Data = data };
    }
}
