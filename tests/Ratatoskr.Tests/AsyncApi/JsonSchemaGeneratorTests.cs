using AwesomeAssertions;
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
    public void NonNullableString_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableString"];

        IsSingleType(prop, "string").Should().BeTrue();
    }

    [Test]
    public void NullableString_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableString"];

        IsNullableType(prop, "string").Should().BeTrue();
    }

    [Test]
    public void NonNullableInt_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableInt"];

        IsSingleType(prop, "integer").Should().BeTrue();
    }

    [Test]
    public void NullableInt_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableInt"];

        IsNullableType(prop, "integer").Should().BeTrue();
    }

    [Test]
    public void NonNullableBool_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableBool"];

        IsSingleType(prop, "boolean").Should().BeTrue();
    }

    [Test]
    public void NullableBool_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableBool"];

        IsNullableType(prop, "boolean").Should().BeTrue();
    }

    [Test]
    public void NonNullableGuid_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableGuid"];

        IsSingleType(prop, "string").Should().BeTrue();
        prop.Format.Should().Be("uuid");
    }

    [Test]
    public void NullableGuid_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableGuid"];

        IsNullableType(prop, "string").Should().BeTrue();
        prop.Format.Should().Be("uuid");
    }

    [Test]
    public void NonNullableDateTime_ProducesSingleType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nonNullableDateTime"];

        IsSingleType(prop, "string").Should().BeTrue();
        prop.Format.Should().Be("date-time");
    }

    [Test]
    public void NullableDateTime_ProducesNullableType()
    {
        var schema = GenerateSchema<PrimitiveNullabilityModel>();
        var prop = schema.Properties!["nullableDateTime"];

        IsNullableType(prop, "string").Should().BeTrue();
        prop.Format.Should().Be("date-time");
    }

    // ──────────────────────────────
    //  Collection nullability
    // ──────────────────────────────

    [Test]
    public void NonNullableList_ProducesArrayWithoutNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nonNullableList"];

        IsSingleType(prop, "array").Should().BeTrue();
        prop.OneOf.Should().BeNull();
    }

    [Test]
    public void NullableList_ProducesOneOfArrayAndNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nullableList"];

        IsOneOfWithNull(prop).Should().BeTrue();
        IsSingleType(prop.OneOf![0], "array").Should().BeTrue();
    }

    [Test]
    public void NonNullableArray_ProducesArrayWithoutNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nonNullableArray"];

        IsSingleType(prop, "array").Should().BeTrue();
        prop.OneOf.Should().BeNull();
    }

    [Test]
    public void NullableArray_ProducesOneOfArrayAndNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nullableArray"];

        IsOneOfWithNull(prop).Should().BeTrue();
        IsSingleType(prop.OneOf![0], "array").Should().BeTrue();
    }

    [Test]
    public void NonNullableDictionary_ProducesObjectWithoutNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nonNullableDictionary"];

        IsSingleType(prop, "object").Should().BeTrue();
        prop.AdditionalProperties.Should().NotBeNull();
        prop.OneOf.Should().BeNull();
    }

    [Test]
    public void NullableDictionary_ProducesOneOfObjectAndNull()
    {
        var schema = GenerateSchema<CollectionNullabilityModel>();
        var prop = schema.Properties!["nullableDictionary"];

        IsOneOfWithNull(prop).Should().BeTrue();
        IsSingleType(prop.OneOf![0], "object").Should().BeTrue();
        prop.OneOf[0].AdditionalProperties.Should().NotBeNull();
    }

    // ──────────────────────────────
    //  Complex object nullability
    // ──────────────────────────────

    [Test]
    public void NonNullableComplexType_ProducesRefWithoutNull()
    {
        var components = new Dictionary<string, JsonSchema>();
        _generator.GenerateAndRegister(typeof(ComplexNullabilityModel), components);
        var schema = components[nameof(ComplexNullabilityModel)];
        var prop = schema.Properties!["nonNullableNested"];

        prop.Ref.Should().Be("#/components/schemas/NestedModel");
        prop.OneOf.Should().BeNull();
    }

    [Test]
    public void NullableComplexType_ProducesOneOfRefAndNull()
    {
        var components = new Dictionary<string, JsonSchema>();
        _generator.GenerateAndRegister(typeof(ComplexNullabilityModel), components);
        var schema = components[nameof(ComplexNullabilityModel)];
        var prop = schema.Properties!["nullableNested"];

        IsOneOfWithNull(prop).Should().BeTrue();
        prop.OneOf![0].Ref.Should().Be("#/components/schemas/NestedModel");
    }
}
