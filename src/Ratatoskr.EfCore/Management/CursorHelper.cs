namespace Ratatoskr.EfCore.Management;

internal static class CursorHelper
{
    internal static string EncodeCursor(Guid id) =>
        Base64UrlEncode(id.ToByteArray());

    internal static Guid? DecodeCursor(string cursor)
    {
        try
        {
            var bytes = Base64UrlDecode(cursor);
            return bytes.Length == 16 ? new Guid(bytes) : null;
        }
        catch { return null; }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
