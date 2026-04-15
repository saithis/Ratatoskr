using System.Text;

namespace Ratatoskr.EfCore.Management;

internal static class CursorHelper
{
    internal static string EncodeCursor(Guid id) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(id.ToString()));

    internal static Guid? DecodeCursor(string cursor)
    {
        try
        {
            var bytes = Base64UrlDecode(cursor);
            var str = Encoding.UTF8.GetString(bytes);
            return Guid.TryParse(str, out var id) ? id : null;
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
