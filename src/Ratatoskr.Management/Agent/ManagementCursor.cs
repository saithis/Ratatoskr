using System.Buffers.Binary;

namespace Ratatoskr.Management.Agent;

/// <summary>Encodes the stable sort key used by management list operations.</summary>
internal static class ManagementCursor
{
    private const int Length = 24;

    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, createdAt.UtcTicks);
        id.TryWriteBytes(bytes[8..]);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out (DateTimeOffset CreatedAt, Guid Id) value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor)) return true;

        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != Length) return false;
            value = (new DateTimeOffset(BinaryPrimitives.ReadInt64BigEndian(bytes), TimeSpan.Zero), new Guid(bytes.AsSpan(8)));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
