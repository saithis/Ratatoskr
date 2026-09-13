using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Ratatoskr.Management.Contracts;

/// <summary>
/// The one pagination cursor the control plane uses: a base64url-encoded
/// <c>[version:1][utcTicks:8 big-endian][guid:16]</c>, keyed on ascending <c>(CreatedAt, Id)</c>.
/// </summary>
/// <remarks>
/// Both halves are required. Rows routinely share a timestamp — a batch of messages poisoned by
/// the same outage lands within the same tick — so a timestamp-only cursor would skip or repeat
/// rows at a page boundary. Callers order by <c>(CreatedAt, Id)</c> and compare with
/// <c>(CreatedAt, Id) &gt; (cursor.CreatedAt, cursor.Id)</c>.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ManagementCursor(DateTimeOffset CreatedAt, Guid Id)
{
    /// <summary>Layout version, so a future change can be rejected rather than misread.</summary>
    public const byte Version = 1;

    private const int ByteLength = 25;

    /// <summary>Encodes this cursor for a client to hand back verbatim.</summary>
    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        bytes[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(bytes[1..9], CreatedAt.UtcTicks);
        if (!Id.TryWriteBytes(bytes[9..]))
        {
            throw new InvalidOperationException("Unexpected failure encoding the cursor Guid.");
        }

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes a cursor. Returns <see langword="true"/> with a null <paramref name="cursor"/> for
    /// a null or empty input, meaning "start at the beginning". Returns <see langword="false"/>
    /// for anything malformed, so a caller can answer with
    /// <see cref="ManagementErrorCodes.InvalidCursor"/> rather than silently restarting paging.
    /// </summary>
    public static bool TryDecode(string? encoded, out ManagementCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return true;
        }

        var normalized = encoded.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder == 1)
        {
            // Never a valid base64 length, and Convert would throw rather than return false.
            return false;
        }

        normalized = remainder switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        Span<byte> bytes = stackalloc byte[ByteLength + 4];
        if (!Convert.TryFromBase64String(normalized, bytes, out var written) || written != ByteLength)
        {
            return false;
        }

        if (bytes[0] != Version)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes[1..9]);
        if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new ManagementCursor(
            new DateTimeOffset(ticks, TimeSpan.Zero),
            new Guid(bytes[9..ByteLength])
        );
        return true;
    }
}

/// <summary>One page of a keyset-paginated list. A null <see cref="NextCursor"/> means the end.</summary>
/// <remarks>
/// Deliberately carries no total. A total is a second full scan of the filtered set, which is the
/// most expensive part of the query on a retained table; callers that genuinely need one ask for
/// it explicitly through the matching <c>*.count</c> operation.
/// </remarks>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
