namespace Ratatoskr.EfCore;

public class OutboxException(string message) : Exception(message);
