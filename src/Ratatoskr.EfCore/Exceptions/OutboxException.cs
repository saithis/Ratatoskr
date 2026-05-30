namespace Ratatoskr.EfCore;

/// <summary>
/// Base exception for errors that occur in the EF Core outbox transport.
/// </summary>
/// <param name="message">The error message describing the outbox failure.</param>
public class OutboxException(string message) : Exception(message);
