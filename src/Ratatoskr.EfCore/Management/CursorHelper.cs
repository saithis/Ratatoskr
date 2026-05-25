using System.Buffers.Binary;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Encodes/decodes a keyset-pagination cursor as Base64-URL over a fixed 26-byte
/// layout of <c>(timestamp-ticks (8 bytes, little-endian), offset-minutes (2 bytes, little-endian), guid (16 bytes))</c>.
///
/// Both the timestamp and the Id are required so that paging remains stable when
/// multiple rows share the same timestamp: callers must <c>ORDER BY time, id</c>
/// and compare with <c>(time, id) &gt; (cursor.Time, cursor.Id)</c>.
/// </summary>
internal static class CursorHelper
{
    private const int CursorByteLength = 26;

    internal readonly record struct Cursor(DateTimeOffset Time, Guid Id);

    internal static string Encode(DateTimeOffset time, Guid id)
    {
        Span<byte> bytes = stackalloc byte[CursorByteLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes[..8], time.Ticks);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(8, 2), (short)time.Offset.TotalMinutes);
        if (!id.TryWriteBytes(bytes.Slice(10, 16)))
        {
            throw new InvalidOperationException("Unexpected failure encoding Guid for cursor.");
        }

        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Attempts to decode a cursor produced by <see cref="Encode"/>. Returns false
    /// for any malformed input; callers should surface a 400 rather than silently
    /// restarting pagination.
    /// </summary>
    internal static bool TryDecode(string cursor, out Cursor value)
    {
        value = default;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[CursorByteLength];
        if (!TryBase64UrlDecode(cursor, bytes, out var written) || written != CursorByteLength)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[..8]);
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(8, 2));

        try
        {
            var time = new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
            var id = new Guid(bytes.Slice(10, 16));
            value = new Cursor(time, id);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string s, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        var normalized = s.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        Span<byte> decoded = stackalloc byte[CursorByteLength + 4];
        if (!Convert.TryFromBase64String(normalized, decoded, out var written))
        {
            return false;
        }

        if (written > destination.Length)
        {
            return false;
        }

        decoded[..written].CopyTo(destination);
        bytesWritten = written;
        return true;
    }
}
