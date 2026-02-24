namespace Ratatoskr.Local;

public class LocalTransportOptions
{
    public int ChannelCapacity { get; set; } = 1000;
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
