using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RabbitMQ.Client;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Why a command was rejected before it was dispatched.</summary>
internal enum CallerRejection
{
    /// <summary>The caller is trusted.</summary>
    None = 0,

    /// <summary>No <c>user_id</c> was set, so there is no identity to trust.</summary>
    MissingUserId = 1,

    /// <summary>The <c>user_id</c> is not on the allowlist.</summary>
    UnknownCaller = 2,

    /// <summary>A shared secret is configured but the command carried no signature.</summary>
    MissingSignature = 3,

    /// <summary>The signature does not match the envelope.</summary>
    InvalidSignature = 4,
}

/// <summary>
/// Decides whether a command that arrived over the broker may be dispatched.
/// </summary>
/// <remarks>
/// The broker is not an authentication boundary. Write access to <c>*.inbox</c> is granted to
/// every identity in the vhost, so an ordinary service — or a compromised one — can publish a
/// command into any other service's command exchange and have it delivered. The dashboard's
/// authorization policies live at its HTTP edge and protect nothing on this path, so the agent
/// has to decide for itself.
/// </remarks>
internal sealed class RabbitMqCallerAuthenticator(RabbitMqManagementOptions options)
{
    /// <summary>The AMQP header carrying the HMAC, where a shared secret is in use.</summary>
    public const string SignatureHeader = "x-ratatoskr-signature";

    /// <summary>Checks one delivery.</summary>
    public CallerRejection Authenticate(IReadOnlyBasicProperties properties, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (options.AllowUnauthenticatedCallers)
        {
            return CallerRejection.None;
        }

        if (!string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            return VerifySignature(properties, body);
        }

        // The broker refuses a publish whose user_id does not match the authenticated connection,
        // so a user_id that is present is genuinely the sender. One that is absent proves nothing,
        // and must be treated as untrusted rather than as "no objection".
        if (string.IsNullOrEmpty(properties.UserId))
        {
            return CallerRejection.MissingUserId;
        }

        return options.AllowedCallers.Contains(properties.UserId)
            ? CallerRejection.None
            : CallerRejection.UnknownCaller;
    }

    /// <summary>Signs an outgoing command, where a shared secret is in use.</summary>
    public string? Sign(Guid operationId, DateTimeOffset deadline, ReadOnlySpan<byte> body) =>
        string.IsNullOrWhiteSpace(options.SharedSecret)
            ? null
            : Convert.ToBase64String(Compute(options.SharedSecret, operationId, deadline, body));

    private CallerRejection VerifySignature(
        IReadOnlyBasicProperties properties,
        ReadOnlySpan<byte> body
    )
    {
        if (
            properties.Headers is null
            || !properties.Headers.TryGetValue(SignatureHeader, out var raw)
            || raw is null
        )
        {
            return CallerRejection.MissingSignature;
        }

        var presented = raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => null,
        };

        if (
            presented is null
            || !TryReadEnvelopeIdentity(properties, out var operationId, out var deadline)
        )
        {
            return CallerRejection.InvalidSignature;
        }

        Span<byte> decoded = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (
            !Base64.IsValid(presented)
            || !Convert.TryFromBase64String(presented, decoded, out var written)
            || written != HMACSHA256.HashSizeInBytes
        )
        {
            return CallerRejection.InvalidSignature;
        }

        var expected = Compute(options.SharedSecret!, operationId, deadline, body);

        // Fixed-time comparison: a signature check that leaks timing is a signature check an
        // attacker can walk byte by byte.
        return CryptographicOperations.FixedTimeEquals(decoded, expected)
            ? CallerRejection.None
            : CallerRejection.InvalidSignature;
    }

    /// <summary>
    /// Binds the signature to the operation id and the deadline as well as the body, so a captured
    /// command cannot be replayed as a different operation or past its expiry.
    /// </summary>
    private static byte[] Compute(
        string secret,
        Guid operationId,
        DateTimeOffset deadline,
        ReadOnlySpan<byte> body
    )
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var preamble = Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{operationId:N}|{deadline.UtcTicks}|"
            )
        );

        var buffer = new byte[preamble.Length + body.Length];
        preamble.CopyTo(buffer, 0);
        body.CopyTo(buffer.AsSpan(preamble.Length));
        return hmac.ComputeHash(buffer);
    }

    private static bool TryReadEnvelopeIdentity(
        IReadOnlyBasicProperties properties,
        out Guid operationId,
        out DateTimeOffset deadline
    )
    {
        operationId = default;
        deadline = default;

        if (!Guid.TryParse(properties.MessageId, out operationId))
        {
            return false;
        }

        if (
            properties.Headers?.TryGetValue(DeadlineHeader, out var raw) is not true
            || raw is null
        )
        {
            return false;
        }

        var text = raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string value => value,
            _ => null,
        };

        if (!long.TryParse(text, CultureInfo.InvariantCulture, out var ticks))
        {
            return false;
        }

        deadline = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    /// <summary>The AMQP header carrying the signed deadline, in UTC ticks.</summary>
    public const string DeadlineHeader = "x-ratatoskr-deadline";
}
