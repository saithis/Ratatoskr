namespace Ratatoskr.Core;

public class ChannelRegistration(string channelName, ChannelType intent)
{
    public string ChannelName { get; } = channelName;
    public ChannelType Intent { get; } = intent;
    
    public Dictionary<string, object> Metadata { get; } = new();
    public List<MessageRegistration> Messages { get; } = new();

    public MessageRegistration? GetMessage(Type messageType)
    {
        return Messages.FirstOrDefault(m => m.MessageType == messageType);
    }
    public MessageRegistration? GetMessage(string messageTypeName)
    {
        return Messages.FirstOrDefault(m => m.MessageTypeName == messageTypeName);
    }
}
