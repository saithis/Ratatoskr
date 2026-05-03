namespace NotificationService;

/// <summary>
/// Dev-only: when enabled, <see cref="Handlers.OrderPlacedNotificationHandler"/> throws so Rabbit transport retries and DLQ apply.
/// </summary>
public sealed class NotificationFailureState
{
    private volatile bool _enabled;

    public bool IsEnabled => _enabled;

    public bool Toggle()
    {
        _enabled = !_enabled;
        return _enabled;
    }
}
