using Ratatoskr.AsyncApi.Model;
using Ratatoskr.AsyncApi.Schema;

namespace Ratatoskr.Tests.AsyncApi;

public class JsonSchemaGeneratorTests
{
    private readonly JsonSchemaGenerator _generator = new();

    // ──────────────────────────────
    //  Test model types
    // ──────────────────────────────

    // ReSharper disable UnusedAutoPropertyAccessor.Local — used via reflection by JsonSchemaGenerator

    /// <summary>Covers primitive and string nullability scenarios.</summary>
    private class PrimitiveNullabilityModel
    {
        public string NonNullableString { get; set; } = "";
        public string? NullableString { get; set; }
        public int NonNullableInt { get; set; }
        public int? NullableInt { get; set; }
        public bool NonNullableBool { get; set; }
        public bool? NullableBool { get; set; }
        public Guid NonNullableGuid { get; set; }
        public Guid? NullableGuid { get; set; }
        public DateTime NonNullableDateTime { get; set; }
        public DateTime? NullableDateTime { get; set; }
    }

    /// <summary>Covers collection nullability scenarios.</summary>
    private class CollectionNullabilityModel
    {
        public List<string> NonNullableList { get; set; } = [];
        public List<string>? NullableList { get; set; }
        public string[] NonNullableArray { get; set; } = [];
        public string[]? NullableArray { get; set; }
        public Dictionary<string, int> NonNullableDictionary { get; set; } = new();
        public Dictionary<string, int>? NullableDictionary { get; set; }
    }

    /// <summary>A nested complex type for reference testing.</summary>
    private class NestedModel
    {
        public string Value { get; set; } = "";
    }

    /// <summary>Covers complex object (reference type) nullability scenarios.</summary>
    private class ComplexNullabilityModel
    {
        public NestedModel NonNullableNested { get; set; } = new();
        public NestedModel? NullableNested { get; set; }
    }

    // ReSharper restore UnusedAutoPropertyAccessor.Local

    // ──────────────────────────────
    //  Helpers
    // ──────────────────────────────

    private JsonSchema GenerateSchema<T>()
    {
        var components = new Dictionary<string, JsonSchema>();
        _generator.GenerateAndRegister(typeof(T), components);
        return components[typeof(T).Name];
    }

    /// <summary>Returns true if the schema type is a single string (non-nullable primitive).</summary>
    private static bool IsSingleType(JsonSchema schema, string expectedType) =>
        schema.Type is string s && s == expectedType;

    /// <summary>Returns true if the schema type is a two-element array including "null" (nullable primitive).</summary>
    private static bool IsNullableType(JsonSchema schema, string expectedType) =>
        schema.Type is string[] arr && arr.Length == 2 && arr[0] == expectedType && arr[1] == "null";

    /// <summary>Returns true if the schema is a oneOf with [innerSchema, {type: "null"}] (nullable complex/collection).</summary>
    private static bool IsOneOfWithNull(JsonSchema schema) =>
        schema.OneOf is { Count: 2 } && schema.OneOf[1].Type is "null";

    // ──────────────────────────────
    //  Primitive nullability
    // ──────────────────────────────

    [Test]
    public async Task NonNullableString_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableString"];

        await Assert.That(IsSingleType(prop, "string")).IsTrue();
    }

    [Test]
    public async Task NullableString_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableString"];

        await Assert.That(IsNullableType(prop, "string")).IsTrue();
    }

    [Test]
    public async Task NonNullableInt_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableInt"];

        await Assert.That(IsSingleType(prop, "integer")).IsTrue();
    }

    [Test]
    public async Task NullableInt_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableInt"];

        await Assert.That(IsNullableType(prop, "integer")).IsTrue();
    }

    [Test]
    public async Task NonNullableBool_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableBool"];

        await Assert.That(IsSingleType(prop, "boolean")).IsTrue();
    }

    [Test]
    public async Task NullableBool_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableBool"];

        await Assert.That(IsNullableType(prop, "boolean")).IsTrue();
    }

    [Test]
    public async Task NonNullableGuid_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableGuid"];

        await Assert.That(IsSingleType(prop, "string")).IsTrue();
        await Assert.That(prop.Format).IsEqualTo("uuid");
    }

    [Test]
    public async Task NullableGuid_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableGuid"];

        await Assert.That(IsNullableType(prop, "string")).IsTrue();
        await Assert.That(prop.Format).IsEqualTo("uuid");
    }

    [Test]
    public async Task NonNullableDateTime_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableDateTime"];

        await Assert.That(IsSingleType(prop, "string")).IsTrue();
        await Assert.That(prop.Format).IsEqualTo("date-time");
    }

    [Test]
    public async Task NullableDateTime_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableDateTime"];

        await Assert.That(IsNullableType(prop, "string")).IsTrue();
        await Assert.That(prop.Format).IsEqualTo("date-time");
    }

    // ──────────────────────────────
    //  Collection nullability
    // ──────────────────────────────

    [Test]
    public async Task NonNullableList_ProducesArrayWithoutNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nonNullableList"];

        await Assert.That(IsSingleType(prop, "array")).IsTrue();
        await Assert.That(prop.OneOf).IsNull();
    }

    [Test]
    public async Task NullableList_ProducesOneOfArrayAndNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nullableList"];

        await Assert.That(IsOneOfWithNull(prop)).IsTrue();
        await Assert.That(IsSingleType(prop.OneOf![0], "array")).IsTrue();
    }

    [Test]
    public async Task NonNullableArray_ProducesArrayWithoutNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nonNullableArray"];

        await Assert.That(IsSingleType(prop, "array")).IsTrue();
        await Assert.That(prop.OneOf).IsNull();
    }

    [Test]
    public async Task NullableArray_ProducesOneOfArrayAndNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nullableArray"];

        await Assert.That(IsOneOfWithNull(prop)).IsTrue();
        await Assert.That(IsSingleType(prop.OneOf![0], "array")).IsTrue();
    }

    [Test]
    public async Task NonNullableDictionary_ProducesObjectWithoutNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nonNullableDictionary"];

        await Assert.That(IsSingleType(prop, "object")).IsTrue();
        await Assert.That(prop.AdditionalProperties).IsNotNull();
        await Assert.That(prop.OneOf).IsNull();
    }

    [Test]
    public async Task NullableDictionary_ProducesOneOfObjectAndNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nullableDictionary"];

        await Assert.That(IsOneOfWithNull(prop)).IsTrue();
        await Assert.That(IsSingleType(prop.OneOf![0], "object")).IsTrue();
        await Assert.That(prop.OneOf[0].AdditionalProperties).IsNotNull();
    }

    // ──────────────────────────────
    //  Complex object nullability
    // ──────────────────────────────

    [Test]
    public async Task NonNullableComplexType_ProducesRefWithoutNull()
    {
        var components = new Dictionary<string, JsonSchema>();
        _generator.GenerateAndRegister(typeof(ComplexNullabilityModel), components);
        var schema = components[nameof(ComplexNullabilityModel)];
        var prop = schema.Properties!["nonNullableNested"];

        await Assert.That(prop.Ref).IsEqualTo("#/components/schemas/NestedModel");
        await Assert.That(prop.OneOf).IsNull();
    }

    [Test]
    public async Task NullableComplexType_ProducesOneOfRefAndNull()
    {
        var components = new Dictionary<string, JsonSchema>();
        _generator.GenerateAndRegister(typeof(ComplexNullabilityModel), components);
        var schema = components[nameof(ComplexNullabilityModel)];
        var prop = schema.Properties!["nullableNested"];

        await Assert.That(IsOneOfWithNull(prop)).IsTrue();
        await Assert.That(prop.OneOf![0].Ref).IsEqualTo("#/components/schemas/NestedModel");
    }
}
