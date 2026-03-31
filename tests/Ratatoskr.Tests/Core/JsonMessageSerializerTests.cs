using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.Core;
using Ratatoskr.Serializers.Json;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class JsonMessageSerializerTests
{
    [Test]
    public void Serialize_WritesProperJson()
    {
        // Arrange
        var serializer = new JsonMessageSerializer();
        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        
        // Act
        var body = serializer.Serialize(testEvent);
        var result = JsonSerializer.Deserialize<TestEvent>(body);
        
        // Assert
        serializer.ContentType.Should().Be("application/json");
        result.Should().NotBeNull();
        result.Id.Should().Be("123");
        result.Data.Should().Be("test data");
    }
    
    [Test]
    public void Serialize_NullData_ReturnsNull()
    {
        // Arrange
        var serializer = new JsonMessageSerializer();
        
        // Act
        var body = serializer.Serialize(null!);
        var result = JsonSerializer.Deserialize<TestEvent>(body);
        
        // Assert
        serializer.ContentType.Should().Be("application/json");
        result.Should().BeNull();
    }
    
    [Test]
    public void Deserialize_ExtractsDataFromBody()
    {
        // Arrange
        var deserializer = new JsonMessageSerializer();
        
        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        
        // Act
        var result = deserializer.Deserialize<TestEvent>(body);
        
        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be("123");
        result.Data.Should().Be("test data");
    }

    [Test]
    public void Deserialize_NullData_ReturnsNull()
    {
        // Arrange
        var deserializer = new JsonMessageSerializer();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize((TestEvent?)null));
        
        // Act
        var result = deserializer.Deserialize<TestEvent>(body);
        
        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void Deserialize_NonGeneric_ReturnsCorrectType()
    {
        // Arrange
        var deserializer = new JsonMessageSerializer();
        
        var testEvent = new TestEvent { Id = "123", Data = "test data" };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testEvent));
        
        // Act
        var result = deserializer.Deserialize(body, typeof(TestEvent));
        
        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<TestEvent>();
        var typedResult = (TestEvent)result;
        typedResult.Id.Should().Be("123");
        typedResult.Data.Should().Be("test data");
    }

    [Test]
    public void Deserialize_ComplexType_DeserializesAllProperties()
    {
        // Arrange
        var deserializer = new JsonMessageSerializer();
        
        var orderId = Guid.NewGuid();
        var orderEvent = new OrderCreatedEvent
        {
            OrderId = orderId,
            Amount = 123.45m,
            CreatedAt = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero)
        };
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(orderEvent));

        // Act
        var result = deserializer.Deserialize<OrderCreatedEvent>(body);

        // Assert
        result.Should().NotBeNull();
        result.OrderId.Should().Be(orderId);
        result.Amount.Should().Be(123.45m);
        result.CreatedAt.Should().Be(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero));
    }

    [Test]
    public void Deserialize_MalformedJson_ThrowsJsonException()
    {
        // Arrange
        var serializer = new JsonMessageSerializer();
        var malformedBody = Encoding.UTF8.GetBytes("{ not valid json }");

        // Act
        var act = () => serializer.Deserialize<TestEvent>(malformedBody);

        // Assert
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void Deserialize_WrongType_ThrowsJsonException()
    {
        // Arrange
        var serializer = new JsonMessageSerializer();
        var body = Encoding.UTF8.GetBytes("[1, 2, 3]");

        // Act
        var act = () => serializer.Deserialize<TestEvent>(body);

        // Assert
        act.Should().Throw<JsonException>();
    }

    [Test]
    public void Serialize_DefaultOptions_ProducesPascalCaseJson()
    {
        // Arrange
        var serializer = new JsonMessageSerializer();
        var testEvent = new TestEvent { Id = "123", Data = "test data" };

        // Act
        var body = serializer.Serialize(testEvent);
        var json = Encoding.UTF8.GetString(body);

        // Assert
        json.Should().Contain("\"Id\":");
        json.Should().Contain("\"Data\":");
        json.Should().NotContain("\"id\":");
        json.Should().NotContain("\"data\":");
    }

    [Test]
    public void Serialize_WithCamelCaseOptions_ProducesCamelCaseJson()
    {
        // Arrange
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var serializer = new JsonMessageSerializer(options);
        var testEvent = new TestEvent { Id = "123", Data = "test data" };

        // Act
        var body = serializer.Serialize(testEvent);
        var json = Encoding.UTF8.GetString(body);

        // Assert
        json.Should().Contain("\"id\":");
        json.Should().Contain("\"data\":");
        json.Should().NotContain("\"Id\":");
        json.Should().NotContain("\"Data\":");
    }

    [Test]
    public void Deserialize_WithCamelCaseOptions_ReadsFromCamelCaseJson()
    {
        // Arrange
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var serializer = new JsonMessageSerializer(options);
        var body = Encoding.UTF8.GetBytes("""{"id":"456","data":"camel case data"}""");

        // Act
        var result = serializer.Deserialize<TestEvent>(body);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("456");
        result.Data.Should().Be("camel case data");
    }
}
