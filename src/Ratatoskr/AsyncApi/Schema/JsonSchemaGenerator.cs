using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;
using Ratatoskr.AsyncApi.Model;

namespace Ratatoskr.AsyncApi.Schema;

/// <summary>
/// Generates JSON Schema objects from CLR types, respecting System.ComponentModel.DataAnnotations attributes.
/// </summary>
public class JsonSchemaGenerator
{
    private static readonly HashSet<Type> _primitiveTypes = new()
    {
        typeof(bool),
        typeof(byte), typeof(sbyte),
        typeof(short), typeof(ushort),
        typeof(int), typeof(uint),
        typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal),
        typeof(char),
        typeof(string),
        typeof(Guid),
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly),
        typeof(Uri),
        typeof(object),
    };

    /// <summary>
    /// Generates schemas for the given types, adding them to the provided components dictionary.
    /// Returns a $ref schema for the given root type.
    /// </summary>
    public JsonSchema GenerateAndRegister(Type type, Dictionary<string, JsonSchema> components)
    {
        var coreType = UnwrapNullable(type);

        if (IsPrimitive(coreType))
            return BuildPrimitiveSchema(coreType, type != coreType);

        var name = GetSchemaName(coreType);
        if (!components.ContainsKey(name))
            GenerateObject(coreType, name, components);

        return JsonSchema.RefTo(name);
    }

    private void GenerateObject(Type type, string name, Dictionary<string, JsonSchema> components)
    {
        // Placeholder prevents infinite recursion for self-referential types
        components[name] = new JsonSchema { Type = "object" };
        var schema = BuildObjectSchema(type, components);
        components[name] = schema;
    }

    private JsonSchema BuildObjectSchema(Type type, Dictionary<string, JsonSchema> components)
    {
        if (type.IsEnum)
        {
            return BuildEnumSchema(type);
        }

        var properties = new Dictionary<string, JsonSchema>();
        var required = new List<string>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always }) continue;

            var propName = GetPropertyName(prop);
            var propSchema = BuildPropertySchema(prop, components);
            properties[propName] = propSchema;

            if (prop.GetCustomAttribute<RequiredAttribute>() != null)
                required.Add(propName);
        }

        return new JsonSchema
        {
            Type = "object",
            Properties = properties.Count > 0 ? properties : null,
            Required = required.Count > 0 ? required : null,
        };
    }

    private JsonSchema BuildPropertySchema(PropertyInfo prop, Dictionary<string, JsonSchema> components)
    {
        var schema = BuildTypeSchema(prop.PropertyType, components);
        ApplyDataAnnotations(prop, schema);
        return schema;
    }

    private JsonSchema BuildTypeSchema(Type type, Dictionary<string, JsonSchema> components)
    {
        var underlying = UnwrapNullable(type);
        bool isNullable = underlying != type || IsReferenceTypeNullable(type);

        // Enumerables (except string)
        if (underlying != typeof(string) && TryGetEnumerableElementType(underlying, out var elementType))
        {
            var itemSchema = BuildTypeSchemaRef(elementType!, components);
            return new JsonSchema { Type = "array", Items = itemSchema };
        }

        if (IsPrimitive(underlying))
        {
            var s = BuildPrimitiveSchema(underlying, isNullable);
            return s;
        }

        // Complex object — use $ref
        var name = GetSchemaName(underlying);
        if (!components.ContainsKey(name))
            GenerateObject(underlying, name, components);

        var refSchema = JsonSchema.RefTo(name);
        if (isNullable)
            return new JsonSchema { OneOf = [refSchema, new JsonSchema { Type = "null" }] };
        return refSchema;
    }

    private JsonSchema BuildTypeSchemaRef(Type type, Dictionary<string, JsonSchema> components)
    {
        var underlying = UnwrapNullable(type);
        if (IsPrimitive(underlying))
            return BuildPrimitiveSchema(underlying, underlying != type);
        return BuildTypeSchema(type, components);
    }

    private static JsonSchema BuildPrimitiveSchema(Type type, bool nullable)
    {
        var schema = type switch
        {
            _ when type == typeof(bool) => new JsonSchema { Type = "boolean" },
            _ when type == typeof(byte) || type == typeof(sbyte) => new JsonSchema { Type = "integer", Format = "int32" },
            _ when type == typeof(short) || type == typeof(ushort) => new JsonSchema { Type = "integer", Format = "int32" },
            _ when type == typeof(int) || type == typeof(uint) => new JsonSchema { Type = "integer", Format = "int32" },
            _ when type == typeof(long) || type == typeof(ulong) => new JsonSchema { Type = "integer", Format = "int64" },
            _ when type == typeof(float) => new JsonSchema { Type = "number", Format = "float" },
            _ when type == typeof(double) => new JsonSchema { Type = "number", Format = "double" },
            _ when type == typeof(decimal) => new JsonSchema { Type = "number" },
            _ when type == typeof(char) => new JsonSchema { Type = "string", MaxLength = 1 },
            _ when type == typeof(string) => new JsonSchema { Type = "string" },
            _ when type == typeof(Guid) => new JsonSchema { Type = "string", Format = "uuid" },
            _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => new JsonSchema { Type = "string", Format = "date-time" },
            _ when type == typeof(DateOnly) => new JsonSchema { Type = "string", Format = "date" },
            _ when type == typeof(TimeOnly) => new JsonSchema { Type = "string", Format = "time" },
            _ when type == typeof(Uri) => new JsonSchema { Type = "string", Format = "uri" },
            _ when type == typeof(object) => new JsonSchema { Type = "object" },
            _ => new JsonSchema { Type = "string" },
        };

        if (nullable && schema.Type is string typeName)
            schema.Type = new[] { typeName, "null" };

        return schema;
    }

    private static JsonSchema BuildEnumSchema(Type type)
    {
        var underlyingType = System.Enum.GetUnderlyingType(type);
        var names = System.Enum.GetNames(type);
        var values = System.Enum.GetValues(type);
        var enumValues = new List<object>(values.Length);
        foreach (var v in values)
            enumValues.Add(Convert.ChangeType(v, underlyingType));

        var format = underlyingType == typeof(long) || underlyingType == typeof(ulong) ? "int64" : "int32";

        return new JsonSchema
        {
            Type = "integer",
            Format = format,
            Enum = enumValues,
            XEnumNames = names.ToList(),
            XEnumVarnames = names.ToList(),
        };
    }

    private static void ApplyDataAnnotations(PropertyInfo prop, JsonSchema schema)
    {
        if (prop.GetCustomAttribute<MaxLengthAttribute>() is { } maxLen)
            schema.MaxLength = maxLen.Length;

        if (prop.GetCustomAttribute<MinLengthAttribute>() is { } minLen)
            schema.MinLength = minLen.Length;

        if (prop.GetCustomAttribute<StringLengthAttribute>() is { } strLen)
        {
            schema.MaxLength = strLen.MaximumLength;
            if (strLen.MinimumLength > 0)
                schema.MinLength = strLen.MinimumLength;
        }

        if (prop.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            if (range.Minimum is double minVal) schema.Minimum = minVal;
            else if (range.Minimum is int minInt) schema.Minimum = minInt;
            if (range.Maximum is double maxVal) schema.Maximum = maxVal;
            else if (range.Maximum is int maxInt) schema.Maximum = maxInt;
        }

        if (prop.GetCustomAttribute<EmailAddressAttribute>() != null)
            schema.Format = "email";

        if (prop.GetCustomAttribute<UrlAttribute>() != null)
            schema.Format = "uri";

        if (prop.GetCustomAttribute<RegularExpressionAttribute>() is { } regex)
            schema.Pattern = regex.Pattern;
    }

    private static bool TryGetEnumerableElementType(Type type, out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IEnumerable<>) || def == typeof(ICollection<>) ||
                def == typeof(IList<>) || def == typeof(List<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>) ||
                def == typeof(HashSet<>) || def == typeof(ISet<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        // Check implemented interfaces
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = typeof(object);
            return true;
        }

        elementType = null;
        return false;
    }

    private static bool IsPrimitive(Type type) => _primitiveTypes.Contains(type);

    private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsReferenceTypeNullable(Type type) => !type.IsValueType;

    private static string GetSchemaName(Type type)
    {
        if (type.IsGenericType)
        {
            var baseName = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join("", type.GetGenericArguments().Select(GetSchemaName));
            return $"{baseName}Of{args}";
        }
        return type.Name;
    }

    private static string GetPropertyName(PropertyInfo prop)
    {
        var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (jsonAttr != null) return jsonAttr.Name;

        // camelCase by default (matches STJ default behavior)
        var name = prop.Name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
