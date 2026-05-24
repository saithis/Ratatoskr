using System.Text.Json;

namespace Ratatoskr.Tests;

internal static class JsonElementTestExtensions
{
    public static List<JsonElement> ToElementList(this JsonElement arrayElement)
    {
        using var enumerator = arrayElement.EnumerateArray();
        return enumerator.ToList();
    }

    public static JsonElement FirstElement(this JsonElement arrayElement)
    {
        using var enumerator = arrayElement.EnumerateArray();
        return enumerator.First();
    }
}
